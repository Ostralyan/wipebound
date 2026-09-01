//! Process entry point. The service itself lives in the library beside this, so
//! integration tests can drive the real router without binding a port.

use std::sync::Arc;

use wipebound_backend::{config::Config, db, router, AppState};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    dotenvy::dotenv().ok();

    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "wipebound_backend=info,tower_http=info".into()),
        )
        .init();

    let config = Arc::new(Config::from_env()?);
    let pool = db::build_pool(&config.database_url)?;
    let state = AppState {
        pool,
        config: Arc::clone(&config),
    };

    let listener = tokio::net::TcpListener::bind(&config.bind_addr).await?;
    tracing::info!(addr = %config.bind_addr, "wipebound backend listening");

    axum::serve(listener, router(state))
        .with_graceful_shutdown(shutdown())
        .await?;

    Ok(())
}

async fn shutdown() {
    let _ = tokio::signal::ctrl_c().await;
    tracing::info!("shutting down");
}
