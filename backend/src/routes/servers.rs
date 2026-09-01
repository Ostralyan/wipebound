use axum::{extract::State, Json};
use diesel::prelude::*;
use diesel_async::RunQueryDsl;
use serde::Deserialize;
use serde_json::{json, Value};

use crate::{error::AppError, schema::game_servers, AppState};

#[derive(Debug, Deserialize)]
pub struct Heartbeat {
    pub id: String,
    #[serde(default = "unknown_region")]
    pub region: String,
}

fn unknown_region() -> String {
    "unknown".to_string()
}

/// POST /v1/internal/servers/heartbeat
///
/// A registry of which servers are alive. Not load balancing and not
/// orchestration -- just enough to answer "who submitted this, and are they
/// still there" when a run looks strange.
pub async fn heartbeat(
    State(state): State<AppState>,
    Json(beat): Json<Heartbeat>,
) -> Result<Json<Value>, AppError> {
    if beat.id.is_empty() || beat.id.len() > 64 {
        return Err(AppError::Invalid("server id is missing or too long".into()));
    }

    let mut conn = state.pool.get().await?;

    diesel::insert_into(game_servers::table)
        .values((
            game_servers::id.eq(&beat.id),
            game_servers::region.eq(&beat.region),
        ))
        .on_conflict(game_servers::id)
        .do_update()
        .set((
            game_servers::region.eq(&beat.region),
            game_servers::last_seen_at.eq(diesel::dsl::now),
        ))
        .execute(&mut conn)
        .await?;

    Ok(Json(json!({ "status": "ok" })))
}
