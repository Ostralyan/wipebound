use chrono::{DateTime, Utc};
use diesel::prelude::*;
use serde::Serialize;

use crate::schema::{run_ability_stats, run_logs, run_player_stats, run_players, runs};

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
    pub player_id: String,
    pub display_name: String,
    pub identity: String,
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
    pub player_id: String,
    pub display_name: String,
    pub identity: String,
}

#[derive(Debug, Insertable)]
#[diesel(table_name = run_logs)]
pub struct NewRunLog {
    pub run_id: String,
    pub format: i32,
    pub body: Vec<u8>,
    pub byte_size: i64,
    pub events: i32,
    pub truncated: bool,
}

#[derive(Debug, Insertable)]
#[diesel(table_name = run_player_stats)]
pub struct NewPlayerStat {
    pub run_id: String,
    pub combat_id: i64,
    pub player_id: String,
    pub display_name: String,
    pub class_name: String,
    pub damage_done: i64,
    pub healing_done: i64,
    pub overhealing: i64,
    pub damage_taken: i64,
    pub damage_absorbed: i64,
    pub avoidable_damage: i64,
    pub interrupts: i32,
    pub dispels: i32,
    pub deaths: i32,
    pub alive_ms: i64,
}

#[derive(Debug, Queryable, Selectable, Serialize)]
#[diesel(table_name = run_player_stats)]
#[diesel(check_for_backend(diesel::pg::Pg))]
pub struct PlayerStatRow {
    pub run_id: String,
    pub combat_id: i64,
    pub player_id: String,
    pub display_name: String,
    pub class_name: String,
    pub damage_done: i64,
    pub healing_done: i64,
    pub overhealing: i64,
    pub damage_taken: i64,
    pub damage_absorbed: i64,
    pub avoidable_damage: i64,
    pub interrupts: i32,
    pub dispels: i32,
    pub deaths: i32,
    pub alive_ms: i64,
}

#[derive(Debug, Insertable)]
#[diesel(table_name = run_ability_stats)]
pub struct NewAbilityStat {
    pub run_id: String,
    pub combat_id: i64,
    pub ability: String,
    pub damage: i64,
    pub healing: i64,
    pub hits: i32,
    pub casts: i32,
}

#[derive(Debug, Queryable, Selectable, Serialize)]
#[diesel(table_name = run_ability_stats)]
#[diesel(check_for_backend(diesel::pg::Pg))]
pub struct AbilityStatRow {
    pub run_id: String,
    pub combat_id: i64,
    pub ability: String,
    pub damage: i64,
    pub healing: i64,
    pub hits: i32,
    pub casts: i32,
}
