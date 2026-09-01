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
    /// Ladders are per balance patch. Omitting it selects the CURRENT one -- never
    /// several at once, because a duration-sorted list spanning different balance
    /// numbers is worse than no ladder: it looks like a ranking and is not one.
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

    // Exactly one hash, always. Defaulting to "every hash we accept" produced a
    // single duration-sorted list spanning incompatible balance numbers, which is
    // worse than no ladder because it looks like one.
    let hash = query
        .content_hash
        .unwrap_or_else(|| state.config.current_content_hash.clone());

    let rows = runs::table
        .filter(runs::boss.eq(boss))
        .filter(runs::rankable.eq(true))
        .filter(runs::content_hash.eq(hash))
        .select(RunRow::as_select())
        .order(runs::duration_ms.asc())
        .limit(limit)
        .load(&mut conn)
        .await?;

    Ok(Json(rows))
}
