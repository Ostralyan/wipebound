use chrono::{DateTime, Utc};
use diesel::prelude::*;
use serde::Serialize;

use crate::schema::{run_players, runs};

#[derive(Debug, Insertable)]
#[diesel(table_name = runs)]
pub struct NewRun {
    pub id: String,
    pub boss: String,
    pub outcome: String,
    pub duration_ms: i64,
    pub content_hash: String,
    pub engine: String,
    pub authority: String,
    pub submission_digest: String,
    pub rankable: bool,
    pub unrankable_reason: Option<String>,
    pub worst_overreach_cm: i64,
    pub game_server: String,
}

#[derive(Debug, Insertable)]
#[diesel(table_name = run_players)]
pub struct NewRunPlayer {
    pub run_id: String,
    pub peer: i64,
    pub damage_done: i64,
    pub healing_done: i64,
    pub damage_taken: i64,
    pub overreach_cm: i64,
}

#[derive(Debug, Queryable, Selectable, Serialize)]
#[diesel(table_name = runs)]
#[diesel(check_for_backend(diesel::pg::Pg))]
pub struct RunRow {
    pub id: String,
    pub boss: String,
    pub outcome: String,
    pub duration_ms: i64,
    pub content_hash: String,
    pub engine: String,
    pub authority: String,
    pub submission_digest: String,
    pub rankable: bool,
    pub unrankable_reason: Option<String>,
    pub worst_overreach_cm: i64,
    pub game_server: String,
    pub created_at: DateTime<Utc>,
}

#[derive(Debug, Queryable, Selectable, Serialize)]
#[diesel(table_name = run_players)]
#[diesel(check_for_backend(diesel::pg::Pg))]
pub struct RunPlayerRow {
    pub run_id: String,
    pub peer: i64,
    pub damage_done: i64,
    pub healing_done: i64,
    pub damage_taken: i64,
    pub overreach_cm: i64,
}
