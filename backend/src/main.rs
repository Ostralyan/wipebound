//! Wipebound's ladder service.
//!
//! Explicitly NOT a game server. The simulation runs in Godot headless, because
//! the rules of an encounter must exist in exactly one implementation -- a second
//! copy of TelegraphArea.Field in another language would be two sources of truth
//! for the same question, with nothing able to keep them honest.
//!
//! This owns what N game servers must agree on: runs, ladders, and which servers
//! are allowed to submit.

mod auth;
mod config;
mod db;
mod domain;
mod error;
mod models;
mod routes;
mod schema;

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

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    dotenvy::dotenv().ok();

    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "wipebound_backend=info,tower_http=info".into()),
        )
        .init();

    let config = Arc::new(config::Config::from_env()?);
    let pool = db::build_pool(&config.database_url)?;
    let state = AppState { pool, config: Arc::clone(&config) };

    let public = Router::new()
        .route("/leaderboards/{boss}", get(routes::leaderboard::top))
        .route("/runs/{id}", get(routes::runs::detail));

    // Everything a game server may do, behind the shared secret. Keeping it to one
    // subtree means the answer to "what can a submitter reach" is one line.
    let internal = Router::new()
        .route("/runs", post(routes::runs::submit))
        .route("/servers/heartbeat", post(routes::servers::heartbeat))
        .route_layer(middleware::from_fn_with_state(
            state.clone(),
            auth::require_game_server,
        ));

    let app = Router::new()
        .route("/health", get(routes::health::health))
        .nest("/v1", public)
        .nest("/v1/internal", internal)
        .layer(TraceLayer::new_for_http())
        .with_state(state);

    let listener = tokio::net::TcpListener::bind(&config.bind_addr).await?;
    tracing::info!(addr = %config.bind_addr, "wipebound backend listening");

    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown())
        .await?;

    Ok(())
}

async fn shutdown() {
    let _ = tokio::signal::ctrl_c().await;
    tracing::info!("shutting down");
}
