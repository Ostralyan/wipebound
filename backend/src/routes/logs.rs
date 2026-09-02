use axum::{
    body::Bytes,
    extract::{Path, State},
    http::{header, HeaderMap, StatusCode},
    response::IntoResponse,
    Json,
};
use diesel::prelude::*;
use diesel_async::{scoped_futures::ScopedFutureExt, AsyncConnection, RunQueryDsl};
use serde_json::{json, Value};
use std::io::Read;

use crate::{
    error::AppError,
    logbook,
    models::{NewAbilityStat, NewPlayerStat, NewRunLog},
    schema::{run_ability_stats, run_logs, run_player_stats, runs},
    AppState,
};

/// A log is mostly repeated integers and compresses about six to one, so a long
/// fight is tens of kilobytes. This is a guard against a mistake, not a budget.
const MAX_COMPRESSED: usize = 8 * 1024 * 1024;
const MAX_DECOMPRESSED: usize = 64 * 1024 * 1024;

/// POST /v1/internal/runs/{id}/log -- the fight behind a run already submitted.
///
/// Separate from the run submission on purpose. The ladder needs four numbers per
/// player and should not wait on a document to get them; and an upload that fails
/// must leave the run standing rather than take it down.
pub async fn upload(
    State(state): State<AppState>,
    Path(id): Path<String>,
    headers: HeaderMap,
    body: Bytes,
) -> Result<(StatusCode, Json<Value>), AppError> {
    if body.len() > MAX_COMPRESSED {
        return Err(AppError::Invalid("combat log is too large".into()));
    }

    let gzipped = headers
        .get(header::CONTENT_ENCODING)
        .and_then(|value| value.to_str().ok())
        .is_some_and(|value| value.eq_ignore_ascii_case("gzip"));

    let json = if gzipped {
        inflate(&body)?
    } else {
        body.to_vec()
    };

    let document = logbook::parse(&json).map_err(|error| AppError::Invalid(error.to_string()))?;
    let derived = logbook::derive(&document);

    let mut conn = state.pool.get().await?;

    // The run has to exist first. A log for a run nobody submitted is either a
    // bug or somebody probing, and either way there is nothing to attach it to.
    let known: i64 = runs::table
        .filter(runs::id.eq(&id))
        .count()
        .get_result(&mut conn)
        .await?;

    if known == 0 {
        return Err(AppError::NotFound);
    }

    let log = NewRunLog {
        run_id: id.clone(),
        format: derived.format,
        body: body.to_vec(),
        byte_size: body.len() as i64,
        events: derived.events,
        truncated: derived.truncated,
    };

    let players: Vec<NewPlayerStat> = derived
        .players
        .iter()
        .map(|player| NewPlayerStat {
            run_id: id.clone(),
            combat_id: player.combat_id,
            player_id: player.player_id.clone(),
            display_name: player.display_name.clone(),
            class_name: player.class_name.clone(),
            damage_done: player.damage_done,
            healing_done: player.healing_done,
            overhealing: player.overhealing,
            damage_taken: player.damage_taken,
            damage_absorbed: player.damage_absorbed,
            avoidable_damage: player.avoidable_damage,
            interrupts: player.interrupts,
            dispels: player.dispels,
            deaths: player.deaths,
            alive_ms: player.alive_ms,
        })
        .collect();

    let abilities: Vec<NewAbilityStat> = derived
        .abilities
        .iter()
        .map(|entry| NewAbilityStat {
            run_id: id.clone(),
            combat_id: entry.combat_id,
            ability: entry.ability.clone(),
            damage: entry.damage,
            healing: entry.healing,
            hits: entry.hits,
            casts: entry.casts,
        })
        .collect();

    let stored = id.clone();
    let summary = json!({
        "run_id": id,
        "events": derived.events,
        "truncated": derived.truncated,
        "players": derived.players.len(),
    });

    // One transaction, and re-uploading replaces rather than duplicating: a
    // server retrying after a timeout must not leave two half-attached logs.
    conn.transaction::<_, diesel::result::Error, _>(|conn| {
        async move {
            diesel::delete(run_logs::table.filter(run_logs::run_id.eq(&stored)))
                .execute(conn)
                .await?;
            diesel::delete(run_player_stats::table.filter(run_player_stats::run_id.eq(&stored)))
                .execute(conn)
                .await?;
            diesel::delete(run_ability_stats::table.filter(run_ability_stats::run_id.eq(&stored)))
                .execute(conn)
                .await?;

            diesel::insert_into(run_logs::table)
                .values(&log)
                .execute(conn)
                .await?;

            if !players.is_empty() {
                diesel::insert_into(run_player_stats::table)
                    .values(&players)
                    .execute(conn)
                    .await?;
            }

            if !abilities.is_empty() {
                diesel::insert_into(run_ability_stats::table)
                    .values(&abilities)
                    .execute(conn)
                    .await?;
            }

            Ok(())
        }
        .scope_boxed()
    })
    .await?;

    Ok((StatusCode::CREATED, Json(summary)))
}

/// GET /v1/runs/{id}/log -- the bytes back, still gzipped.
///
/// Served as stored rather than re-encoded, so a replay reads exactly what the
/// server wrote instead of a round trip through this schema's opinions.
pub async fn download(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<impl IntoResponse, AppError> {
    let mut conn = state.pool.get().await?;

    let body: Vec<u8> = run_logs::table
        .filter(run_logs::run_id.eq(&id))
        .select(run_logs::body)
        .first(&mut conn)
        .await
        .optional()?
        .ok_or(AppError::NotFound)?;

    Ok((
        [
            (header::CONTENT_TYPE, "application/json"),
            (header::CONTENT_ENCODING, "gzip"),
        ],
        body,
    ))
}

/// Bounded, because a gzip bomb is a few kilobytes that becomes a few gigabytes.
fn inflate(compressed: &[u8]) -> Result<Vec<u8>, AppError> {
    let mut out = Vec::new();
    flate2::read::GzDecoder::new(compressed)
        .take(MAX_DECOMPRESSED as u64)
        .read_to_end(&mut out)
        .map_err(|error| AppError::Invalid(format!("combat log is not gzip: {error}")))?;

    Ok(out)
}
