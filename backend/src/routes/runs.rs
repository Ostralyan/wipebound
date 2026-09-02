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
    models::{AbilityStatRow, NewRun, NewRunPlayer, PlayerStatRow, RunPlayerRow, RunRow},
    schema::{run_ability_stats, run_logs, run_player_stats, run_players, runs},
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
        state.config.ranked_require_verified_identity,
    );

    let game_server = headers
        .get("x-server-id")
        .and_then(|value| value.to_str().ok())
        .unwrap_or("unknown")
        .to_string();

    // The submitting server is part of the submission: the same body from a
    // different server is a different submission, not a retry of the first.
    let digest = domain::digest(&submission, &game_server);

    let run = NewRun {
        id: submission.run_id.clone(),
        boss: submission.boss.clone(),
        outcome: submission.outcome.clone(),
        duration_ms: submission.duration_ms,
        content_hash: submission.content_hash.clone(),
        engine: submission.engine.clone(),
        authority: submission.authority.clone(),
        submission_digest: digest.clone(),
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
            player_id: player.player_id.clone(),
            display_name: player.display_name.clone(),
            identity: player.identity.clone(),
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

    // A duplicate must answer with what is STORED, not with a verdict computed from
    // the payload that arrived second. Reporting the new payload's verdict for a
    // run that was never written meant a retry could describe a record that does
    // not exist -- and a DIFFERENT payload reusing an id is not a retry at all, so
    // it is refused rather than silently ignored.
    if inserted == 0 {
        let stored = runs::table
            .find(&submission.run_id)
            .select(RunRow::as_select())
            .first(&mut conn)
            .await
            .optional()?
            .ok_or_else(|| AppError::Internal("run vanished between insert and read".into()))?;

        // One comparison, over everything the submitter said.
        //
        // There is deliberately NO compatibility path for a row without a digest.
        // The column is NOT NULL with no default and shipped before any database
        // existed, so such a row cannot be written -- and the path that used to
        // handle one compared five fields, which was precisely the vulnerability
        // the digest was introduced to close. A compatibility shim that
        // reintroduces the bug it is compatible with is worse than the migration
        // it saves.
        //
        // An empty digest would fail closed, which is the right direction anyway:
        // the run is already stored, so refusing its retry loses nothing.
        if stored.submission_digest != digest {
            return Err(AppError::Conflict(format!(
                "run {} already exists with different contents",
                submission.run_id
            )));
        }

        return Ok((
            StatusCode::OK,
            Json(json!({
                "run_id": stored.id,
                "rankable": stored.rankable,
                "reason": stored.unrankable_reason,
                "duplicate": true,
            })),
        ));
    }

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
/// GET /v1/runs/recent -- the newest attempts, ranked or not.
///
/// A ladder shows the best; this shows the latest, which is what somebody who
/// just finished a fight is looking for.
pub async fn recent(State(state): State<AppState>) -> Result<Json<Vec<RunRow>>, AppError> {
    let mut conn = state.pool.get().await?;

    let rows = runs::table
        .select(RunRow::as_select())
        .order(runs::created_at.desc())
        .limit(50)
        .load(&mut conn)
        .await?;

    Ok(Json(rows))
}

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

    // Derived at upload, so a page showing meters is three indexed reads rather
    // than a gzip inflate and a fold over ten thousand events.
    let stats = run_player_stats::table
        .filter(run_player_stats::run_id.eq(&id))
        .select(PlayerStatRow::as_select())
        .order(run_player_stats::damage_done.desc())
        .load(&mut conn)
        .await?;

    let abilities = run_ability_stats::table
        .filter(run_ability_stats::run_id.eq(&id))
        .select(AbilityStatRow::as_select())
        .order(run_ability_stats::damage.desc())
        .load(&mut conn)
        .await?;

    // Whether there is a fight to replay, without shipping it in this response.
    let has_log: i64 = run_logs::table
        .filter(run_logs::run_id.eq(&id))
        .count()
        .get_result(&mut conn)
        .await?;

    Ok(Json(json!({
        "run": run,
        "players": players,
        "stats": stats,
        "abilities": abilities,
        "has_log": has_log > 0,
    })))
}
