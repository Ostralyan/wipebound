use axum::{
    extract::{Path, Query, State},
    Json,
};
use diesel::prelude::*;
use diesel_async::RunQueryDsl;
use serde::Deserialize;

use crate::{error::AppError, models::RunRow, schema::runs, AppState};

#[derive(Debug, Deserialize)]
pub struct TopQuery {
    /// Ladders are per balance patch. Omitting it does NOT mix seasons -- it falls
    /// back to every hash configured as ranked, because a ladder that quietly
    /// compares runs from different balance numbers is worse than no ladder.
    pub content_hash: Option<String>,
    pub limit: Option<i64>,
}

/// GET /v1/leaderboards/{boss}
pub async fn top(
    State(state): State<AppState>,
    Path(boss): Path<String>,
    Query(query): Query<TopQuery>,
) -> Result<Json<Vec<RunRow>>, AppError> {
    // Clamped rather than validated: a caller asking for a million rows gets a
    // hundred, not an error, and the database is never asked the silly question.
    let limit = query.limit.unwrap_or(50).clamp(1, 100);

    let mut conn = state.pool.get().await?;

    let mut statement = runs::table
        .filter(runs::boss.eq(boss))
        .filter(runs::rankable.eq(true))
        .into_boxed();

    statement = match query.content_hash {
        Some(hash) => statement.filter(runs::content_hash.eq(hash)),
        None => {
            statement.filter(runs::content_hash.eq_any(state.config.ranked_content_hashes.clone()))
        }
    };

    let rows = statement
        .select(RunRow::as_select())
        .order(runs::duration_ms.asc())
        .limit(limit)
        .load(&mut conn)
        .await?;

    Ok(Json(rows))
}
