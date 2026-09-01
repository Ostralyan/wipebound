use axum::{
    extract::{Path, State},
    http::{HeaderMap, StatusCode},
    Json,
};
use diesel::prelude::*;
use diesel_async::{AsyncConnection, RunQueryDsl};
use scoped_futures::ScopedFutureExt;
use serde_json::{json, Value};

use crate::{
    domain::{self, RunSubmission},
    error::AppError,
    models::{NewRun, NewRunPlayer, RunPlayerRow, RunRow},
    schema::{run_players, runs},
    AppState,
};

/// POST /v1/internal/runs -- a game server reporting an attempt.
///
/// The submitter is authenticated but not believed. It reports facts; this
/// decides whether they count, and stores the run either way so an unranked
/// attempt is still telemetry rather than a hole in the record.
pub async fn submit(
    State(state): State<AppState>,
    headers: HeaderMap,
    Json(submission): Json<RunSubmission>,
) -> Result<(StatusCode, Json<Value>), AppError> {
    domain::check(&submission).map_err(|rejection| AppError::Invalid(rejection.to_string()))?;

    let verdict = domain::rank(
        &submission,
        &state.config.ranked_content_hashes,
        state.config.ranked_max_overreach_cm,
    );

    let game_server = headers
        .get("x-server-id")
        .and_then(|value| value.to_str().ok())
        .unwrap_or("unknown")
        .to_string();

    let run = NewRun {
        id: submission.run_id.clone(),
        boss: submission.boss.clone(),
        outcome: submission.outcome.clone(),
        duration_ms: submission.duration_ms,
        content_hash: submission.content_hash.clone(),
        engine: submission.engine.clone(),
        authority: submission.authority.clone(),
        rankable: verdict.rankable,
        unrankable_reason: verdict.reason.map(str::to_string),
        worst_overreach_cm: submission.worst_overreach_cm,
        game_server,
    };

    let lines: Vec<NewRunPlayer> = submission
        .players
        .iter()
        .map(|player| NewRunPlayer {
            run_id: submission.run_id.clone(),
            peer: player.peer,
            damage_done: player.damage_done,
            healing_done: player.healing_done,
            damage_taken: player.damage_taken,
            overreach_cm: player.overreach_cm,
        })
        .collect();

    let mut conn = state.pool.get().await?;

    // One transaction. The run id comes from the game server precisely so a retry
    // after a network failure is harmless -- but inserting the run and its players
    // separately meant a failure between them left a permanent headless run, since
    // the retry saw the run already present and skipped the players. Atomic or not
    // at all.
    let inserted = conn
        .transaction::<i64, diesel::result::Error, _>(|conn| {
            let run = &run;
            let lines = &lines;

            async move {
                // Do-nothing rather than upsert: a run already recorded is settled,
                // and a second copy must not be able to change the first.
                let inserted = diesel::insert_into(runs::table)
                    .values(run)
                    .on_conflict(runs::id)
                    .do_nothing()
                    .execute(conn)
                    .await?;

                if inserted > 0 {
                    diesel::insert_into(run_players::table)
                        .values(lines)
                        .execute(conn)
                        .await?;
                }

                Ok(inserted as i64)
            }
            .scope_boxed()
        })
        .await?;

    tracing::info!(
        run = %submission.run_id,
        boss = %submission.boss,
        outcome = %submission.outcome,
        rankable = verdict.rankable,
        duplicate = inserted == 0,
        "run recorded"
    );

    Ok((
        StatusCode::CREATED,
        Json(json!({
            "run_id": submission.run_id,
            "rankable": verdict.rankable,
            "reason": verdict.reason,
            "duplicate": inserted == 0,
        })),
    ))
}

/// GET /v1/runs/{id}
pub async fn detail(
    State(state): State<AppState>,
    Path(id): Path<String>,
) -> Result<Json<Value>, AppError> {
    let mut conn = state.pool.get().await?;

    let run = runs::table
        .find(&id)
        .select(RunRow::as_select())
        .first(&mut conn)
        .await
        .optional()?
        .ok_or(AppError::NotFound)?;

    let players = run_players::table
        .filter(run_players::run_id.eq(&id))
        .select(RunPlayerRow::as_select())
        .order(run_players::damage_done.desc())
        .load(&mut conn)
        .await?;

    Ok(Json(json!({ "run": run, "players": players })))
}
