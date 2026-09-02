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
    models::{NewAbilityStat, NewPlayerStat, NewRunLog, RunRow},
    schema::{run_ability_stats, run_logs, run_player_stats, run_players, runs},
    AppState,
};

/// A log is mostly repeated integers and compresses about six to one, so a long
/// fight is tens of kilobytes. This is a guard against a mistake, not a budget.
///
/// Public, because the ROUTE has to carry it too. Axum's Bytes extractor applies
/// its own default of two megabytes, so this check alone described a limit that
/// was never reached and quietly refused every upload between two and eight.
pub const MAX_COMPRESSED: usize = 8 * 1024 * 1024;
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

    // Of the DOCUMENT, not the stored bytes. gzip is not deterministic, so two
    // honest uploads of one log can compress differently and must still be
    // recognised as the same evidence.
    let digest = hex::encode(<sha2::Sha256 as sha2::Digest>::digest(&json));

    let mut conn = state.pool.get().await?;

    // The run has to exist first. A log for a run nobody submitted is either a
    // bug or somebody probing, and either way there is nothing to attach it to.
    let run: RunRow = runs::table
        .find(&id)
        .select(RunRow::as_select())
        .first(&mut conn)
        .await
        .optional()?
        .ok_or(AppError::NotFound)?;

    // The whole identity of every seat, not just its number. The derived
    // statistics take player_id and display_name from the uploaded document, so
    // anything unverified here becomes somebody's public history.
    let roster: Vec<(i64, String, String)> = run_players::table
        .filter(run_players::run_id.eq(&id))
        .select((
            run_players::peer,
            run_players::player_id,
            run_players::display_name,
        ))
        .load(&mut conn)
        .await?;

    // Authentication says the sender is a game server we know. It says nothing
    // about whether this document describes THIS run, and without that a server
    // could attach any fight to any run and rewrite its meters.
    logbook::belongs_to(&document, &id, &run.boss, run.duration_ms, &roster)
        .map_err(|error| AppError::Invalid(error.to_string()))?;

    let derived = logbook::derive(&document);

    // Always stored gzipped, so the download's Content-Encoding is always true.
    // A plain-JSON upload used to be stored raw and served with a gzip header,
    // which no browser could read.
    let stored_body = if gzipped {
        body.to_vec()
    } else {
        deflate(&json)?
    };

    let log = NewRunLog {
        run_id: id.clone(),
        format: derived.format,
        byte_size: stored_body.len() as i64,
        body: stored_body,
        events: derived.events,
        truncated: derived.truncated,
        log_digest: digest.clone(),
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
            resource_spent: player.resource_spent,
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
            resource_spent: entry.resource_spent,
        })
        .collect();

    let summary = json!({
        "run_id": id,
        "events": derived.events,
        "truncated": derived.truncated,
        "players": derived.players.len(),
    });

    // THE INSERT DECIDES, not a read before it.
    //
    // Checking for an existing log and then inserting is two steps with a gap:
    // two retries of the same upload both saw nothing, both inserted, and the
    // loser got a primary key violation reported as a server error. Letting the
    // insert resolve the conflict makes the whole decision one atomic step, so a
    // duplicate is a duplicate however many arrive at once.
    let claimed = id.clone();
    let mine = digest.clone();

    let outcome = conn
        .transaction::<Landed, diesel::result::Error, _>(|conn| {
            async move {
                let inserted = diesel::insert_into(run_logs::table)
                    .values(&log)
                    .on_conflict(run_logs::run_id)
                    .do_nothing()
                    .execute(conn)
                    .await?;

                if inserted == 0 {
                    // Somebody else got there. Whether that makes this a retry
                    // or a conflict is theirs to decide, not ours.
                    let existing: String = run_logs::table
                        .filter(run_logs::run_id.eq(&claimed))
                        .select(run_logs::log_digest)
                        .first(conn)
                        .await?;

                    return Ok(if existing == mine {
                        Landed::Duplicate
                    } else {
                        Landed::Conflict
                    });
                }

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

                Ok(Landed::Created)
            }
            .scope_boxed()
        })
        .await?;

    match outcome {
        Landed::Created => Ok((StatusCode::CREATED, Json(summary))),
        Landed::Duplicate => Ok((
            StatusCode::OK,
            Json(json!({ "run_id": id, "duplicate": true })),
        )),

        // Evidence is not rewritable. Different bytes for a run that already has
        // a log are refused, exactly as they are for the run record itself.
        Landed::Conflict => Err(AppError::Conflict(
            "this run already has a different combat log".into(),
        )),
    }
}

/// What the insert turned out to be. Decided inside the transaction, reported
/// outside it, so the HTTP shape is chosen in one place.
enum Landed {
    Created,
    Duplicate,
    Conflict,
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

/// Compress an upload that arrived as plain JSON, so everything in the table is
/// gzip and the download never lies about its encoding.
fn deflate(json: &[u8]) -> Result<Vec<u8>, AppError> {
    use std::io::Write;

    let mut encoder = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    encoder
        .write_all(json)
        .and_then(|()| encoder.finish())
        .map_err(|error| AppError::Internal(format!("could not compress combat log: {error}")))
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
