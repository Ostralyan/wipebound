//! Wipebound's ladder service.
//!
//! Explicitly NOT a game server. The simulation runs in Godot headless, because
//! the rules of an encounter must exist in exactly one implementation -- a second
//! copy of TelegraphArea.Field in another language would be two sources of truth
//! for the same question, with nothing able to keep them honest.
//!
//! This owns what N game servers must agree on: runs, ladders, and which servers
//! are allowed to submit.

pub mod auth;
pub mod config;
pub mod db;
pub mod domain;
pub mod error;
pub mod logbook;
pub mod models;
pub mod routes;
pub mod schema;

use std::sync::Arc;

use axum::{
    middleware,
    routing::{get, post},
    Router,
};
use tower_http::trace::TraceLayer;

#[derive(Clone)]
pub struct AppState {
    pub pool: db::Pool,
    pub config: Arc<config::Config>,
}

/// The whole HTTP surface, with no listener attached, so integration tests can
/// drive exactly what production serves rather than an approximation of it.
pub fn router(state: AppState) -> Router {
    let public = Router::new()
        .route("/leaderboards/{boss}", get(routes::leaderboard::top))
        .route("/runs/recent", get(routes::runs::recent))
        .route("/runs/{id}", get(routes::runs::detail))
        .route("/runs/{id}/log", get(routes::logs::download));

    // Everything a game server may do, behind the shared secret. Keeping it to one
    // subtree means the answer to "what can a submitter reach" is one line.
    let internal = Router::new()
        .route("/runs", post(routes::runs::submit))
        .route("/runs/{id}/log", post(routes::logs::upload))
        .route("/servers/heartbeat", post(routes::servers::heartbeat))
        .route_layer(middleware::from_fn_with_state(
            state.clone(),
            auth::require_game_server,
        ));

    Router::new()
        .route("/health", get(routes::health::health))
        // The site itself. One file, no build step, and served from the same
        // process as the API it reads -- a log viewer that needs its own
        // deployment is a log viewer that drifts out of step with the schema.
        .route(
            "/",
            get(|| async { axum::response::Html(include_str!("../web/index.html")) }),
        )
        .nest("/v1", public)
        .nest("/v1/internal", internal)
        .layer(TraceLayer::new_for_http())
        .with_state(state)
}
