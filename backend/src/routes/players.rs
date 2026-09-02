use axum::{
    extract::{Path, State},
    Json,
};
use diesel::prelude::*;
use diesel_async::RunQueryDsl;
use serde_json::{json, Value};

use crate::{
    error::AppError,
    models::{PlayerStatRow, RunRow},
    schema::{run_player_stats, runs},
    AppState,
};

/// GET /v1/players/{player_id} -- everything this person has been in.
///
/// The question a peer id could never answer, which is the whole reason identity
/// was given to a run. The id is opaque and comes from a run page rather than
/// from a search: a ladder shows who played, it does not offer a directory of
/// people to look up.
pub async fn history(
    State(state): State<AppState>,
    Path(player_id): Path<String>,
) -> Result<Json<Value>, AppError> {
    let mut conn = state.pool.get().await?;

    let stats: Vec<PlayerStatRow> = run_player_stats::table
        .filter(run_player_stats::player_id.eq(&player_id))
        .select(PlayerStatRow::as_select())
        .load(&mut conn)
        .await?;

    if stats.is_empty() {
        return Err(AppError::NotFound);
    }

    let ids: Vec<String> = stats.iter().map(|row| row.run_id.clone()).collect();

    let mut attempts: Vec<RunRow> = runs::table
        .filter(runs::id.eq_any(&ids))
        .select(RunRow::as_select())
        .order(runs::created_at.desc())
        .load(&mut conn)
        .await?;

    // Newest first, and the stats beside the run they belong to rather than in a
    // second list the caller has to zip up.
    let mut paired = Vec::with_capacity(attempts.len());
    for run in attempts.drain(..) {
        let mine = stats.iter().find(|row| row.run_id == run.id);
        paired.push(json!({ "run": run, "stats": mine }));
    }

    let name = stats
        .iter()
        .map(|row| row.display_name.clone())
        .next_back()
        .unwrap_or_default();

    Ok(Json(json!({
        "player_id": player_id,
        "display_name": name,
        "attempts": paired,
    })))
}

/// GET /v1/bosses -- what there is a ladder for.
pub async fn bosses(State(state): State<AppState>) -> Result<Json<Value>, AppError> {
    let mut conn = state.pool.get().await?;

    let rows: Vec<(String, i64)> = runs::table
        .group_by(runs::boss)
        .select((runs::boss, diesel::dsl::count_star()))
        .order(runs::boss.asc())
        .load(&mut conn)
        .await?;

    Ok(Json(json!(rows
        .into_iter()
        .map(|(boss, attempts)| json!({ "boss": boss, "attempts": attempts }))
        .collect::<Vec<_>>())))
}
