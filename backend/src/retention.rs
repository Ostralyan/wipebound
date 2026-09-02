//! Forgetting the fight, but never the numbers.
//!
//! A log is tens of kilobytes and every one of them lives in Postgres, where it
//! also lives in every backup. The derived stats are a few hundred bytes and are
//! what a ladder and a player page actually read.
//!
//! So only the BLOB expires. A run keeps its meters, its ability breakdown and
//! its place on the ladder for ever; what it loses is the ability to be replayed.
//! `has_log` becoming false is the whole visible effect.

use std::time::Duration;

use diesel::prelude::*;
use diesel_async::RunQueryDsl;

use crate::{db::Pool, schema::run_logs};

/// Checked rarely. Nothing here is urgent, and a sweep that runs hourly on a
/// table measured in days is mostly a wasted query.
const SWEEP_EVERY: Duration = Duration::from_secs(6 * 60 * 60);

/// Start the sweeper. Never fails the service: a backend that cannot prune is
/// still a backend that can take runs.
pub fn spawn(pool: Pool, keep_days: i64) {
    if keep_days <= 0 {
        tracing::info!("combat logs are kept indefinitely");
        return;
    }

    tokio::spawn(async move {
        loop {
            match sweep(&pool, keep_days).await {
                Ok(0) => {}
                Ok(dropped) => tracing::info!(dropped, keep_days, "pruned combat logs"),
                Err(error) => tracing::warn!(%error, "could not prune combat logs"),
            }

            tokio::time::sleep(SWEEP_EVERY).await;
        }
    });
}

async fn sweep(
    pool: &Pool,
    keep_days: i64,
) -> Result<usize, Box<dyn std::error::Error + Send + Sync>> {
    let cutoff = chrono::Utc::now() - chrono::Duration::days(keep_days);
    let mut conn = pool.get().await?;

    let dropped = diesel::delete(run_logs::table.filter(run_logs::created_at.lt(cutoff)))
        .execute(&mut conn)
        .await?;

    Ok(dropped)
}
