//! Integration tests over the real router and a real database.
//!
//! These cover what unit tests structurally cannot: routing, the auth layer,
//! transactional persistence, idempotency and the conflict path. Every one of
//! them exercises the same `router()` production serves.
//!
//! They need Postgres. `docker compose up -d`, then:
//!
//!     DATABASE_URL=postgres://wipebound:wipebound@localhost:55432/wipebound cargo test
//!
//! Without DATABASE_URL they PANIC. Skipping has to be deliberate and visible:
//! set WIPEBOUND_SKIP_DB_TESTS=1. A job that quietly reported green without a
//! database proved nothing, which is worse than a red one.

use std::sync::{
    atomic::{AtomicU64, Ordering},
    Arc,
};

use axum::{
    body::Body,
    http::{Request, StatusCode},
};
use diesel_async::RunQueryDsl;
use http_body_util::BodyExt;
use serde_json::{json, Value};
use tower::ServiceExt;
use wipebound_backend::{config::Config, db, router, AppState};

const TOKEN: &str = "integration-token";
const HASH: &str = "hash-current";

static COUNTER: AtomicU64 = AtomicU64::new(0);

/// Distinct per test AND per run, so tests are order independent and repeated
/// runs against the same database never collide.
fn unique(tag: &str) -> String {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0);

    let seq = COUNTER.fetch_add(1, Ordering::Relaxed);
    let mut seed = nanos ^ (seq << 32);
    for byte in tag.bytes() {
        seed = seed.rotate_left(5) ^ u64::from(byte);
    }

    format!("{seed:016x}{:016x}", seed.rotate_right(17))
}

fn state() -> Option<AppState> {
    let database_url = std::env::var("DATABASE_URL").ok()?;

    let config = Config {
        database_url: database_url.clone(),
        bind_addr: "127.0.0.1:0".into(),
        game_server_token: TOKEN.into(),
        ranked_content_hashes: vec![HASH.into(), "hash-previous".into()],
        current_content_hash: HASH.into(),
        ranked_max_overreach_cm: 200,
    };

    Some(AppState {
        pool: db::build_pool(&database_url).ok()?,
        config: Arc::new(config),
    })
}

async fn call(state: AppState, request: Request<Body>) -> (StatusCode, Value) {
    let response = router(state).oneshot(request).await.expect("router failed");
    let status = response.status();
    let bytes = response.into_body().collect().await.unwrap().to_bytes();
    let body = serde_json::from_slice(&bytes).unwrap_or(Value::Null);
    (status, body)
}

fn submit(body: Value, token: Option<&str>) -> Request<Body> {
    let mut builder = Request::builder()
        .method("POST")
        .uri("/v1/internal/runs")
        .header("content-type", "application/json")
        .header("x-server-id", "review-server-a");

    if let Some(token) = token {
        builder = builder.header("authorization", format!("Bearer {token}"));
    }

    builder.body(Body::from(body.to_string())).unwrap()
}

fn run(
    id: &str,
    boss: &str,
    outcome: &str,
    duration_ms: i64,
    hash: &str,
    authority: &str,
) -> Value {
    json!({
        "schema": 1,
        "run_id": id,
        "boss": boss,
        "outcome": outcome,
        "duration_ms": duration_ms,
        "content_hash": hash,
        "engine": "test",
        "authority": authority,
        "worst_overreach_cm": 0,
        "players": [{
            "peer": 1, "damage_done": 100, "healing_done": 0,
            "damage_taken": 10, "overreach_cm": 0
        }],
    })
}

/// FAIL without a database, unless skipping was asked for explicitly.
///
/// Silently passing was worse than useless: a CI job with no database reported
/// eight green tests and proved nothing. Opting out has to be a decision somebody
/// made and can be seen in the job definition.
macro_rules! require_db {
    () => {
        match state() {
            Some(value) => value,
            None if std::env::var("WIPEBOUND_SKIP_DB_TESTS").is_ok() => {
                eprintln!("skipped: WIPEBOUND_SKIP_DB_TESTS is set");
                return;
            }
            None => panic!(
                "DATABASE_URL is not set. Start one with `docker compose up -d`, or set \
                 WIPEBOUND_SKIP_DB_TESTS=1 to skip these on purpose."
            ),
        }
    };
}

#[tokio::test]
async fn health_is_open() {
    let app = require_db!();
    let request = Request::builder()
        .uri("/health")
        .body(Body::empty())
        .unwrap();
    let (status, body) = call(app, request).await;

    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["status"], "ok");
}

#[tokio::test]
async fn submitting_without_the_secret_is_refused() {
    let app = require_db!();
    let id = unique("noauth");
    let (status, _) = call(
        app.clone(),
        submit(run(&id, "B", "kill", 1000, HASH, "dedicated"), None),
    )
    .await;
    assert_eq!(status, StatusCode::UNAUTHORIZED);

    let (status, _) = call(
        app,
        submit(
            run(&id, "B", "kill", 1000, HASH, "dedicated"),
            Some("wrong"),
        ),
    )
    .await;
    assert_eq!(status, StatusCode::UNAUTHORIZED);
}

#[tokio::test]
async fn an_honest_clear_is_stored_and_ranked() {
    let app = require_db!();
    let id = unique("honest");
    let boss = format!("boss-{id}");

    let (status, body) = call(
        app.clone(),
        submit(
            run(&id, &boss, "kill", 60_000, HASH, "dedicated"),
            Some(TOKEN),
        ),
    )
    .await;

    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["rankable"], true);
    assert_eq!(body["duplicate"], false);

    // And the players landed with it, which is what the transaction is for.
    let request = Request::builder()
        .uri(format!("/v1/runs/{id}"))
        .body(Body::empty())
        .unwrap();

    let (status, body) = call(app, request).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["players"].as_array().unwrap().len(), 1);
    assert_eq!(body["players"][0]["damage_done"], 100);
}

#[tokio::test]
async fn an_identical_retry_returns_the_stored_verdict() {
    let app = require_db!();
    let id = unique("retry");
    let boss = format!("boss-{id}");
    let payload = run(&id, &boss, "kill", 42_000, HASH, "dedicated");

    let (status, _) = call(app.clone(), submit(payload.clone(), Some(TOKEN))).await;
    assert_eq!(status, StatusCode::CREATED);

    let (status, body) = call(app, submit(payload, Some(TOKEN))).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["duplicate"], true);
    assert_eq!(body["rankable"], true);
}

#[tokio::test]
async fn a_different_payload_reusing_an_id_is_a_conflict() {
    let app = require_db!();
    let id = unique("conflict");
    let boss = format!("boss-{id}");

    let (status, _) = call(
        app.clone(),
        submit(
            run(&id, &boss, "kill", 42_000, HASH, "dedicated"),
            Some(TOKEN),
        ),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);

    // Not a retry. A retry is the same run; this is a different one wearing its id.
    let (status, _) = call(
        app.clone(),
        submit(
            run(&id, &boss, "wipe", 1, HASH, "player_hosted"),
            Some(TOKEN),
        ),
    )
    .await;
    assert_eq!(status, StatusCode::CONFLICT);

    let request = Request::builder()
        .uri(format!("/v1/runs/{id}"))
        .body(Body::empty())
        .unwrap();

    let (_, body) = call(app, request).await;
    assert_eq!(
        body["run"]["outcome"], "kill",
        "the stored run must be untouched"
    );
    assert_eq!(body["run"]["rankable"], true);
}

/// The case the previous test suite missed entirely: it only ever varied fields
/// that were already being compared, so it passed while a different run wearing a
/// used id was being accepted as a retry.
#[tokio::test]
async fn a_reused_id_is_a_conflict_even_when_the_compared_columns_match() {
    let app = require_db!();
    let id = unique("sneaky");
    let boss = format!("boss-{id}");

    let original = run(&id, &boss, "kill", 42_000, HASH, "dedicated");
    let (status, _) = call(app.clone(), submit(original.clone(), Some(TOKEN))).await;
    assert_eq!(status, StatusCode::CREATED);

    // Outcome, duration, content hash, authority and overreach are all identical.
    // Everything else is different.
    let mut disguised = original.clone();
    disguised["boss"] = json!(format!("{boss}-elsewhere"));
    disguised["engine"] = json!("4.9.9");
    disguised["players"][0]["damage_done"] = json!(999_999);
    disguised["players"][0]["peer"] = json!(4242);

    let (status, _) = call(app.clone(), submit(disguised, Some(TOKEN))).await;
    assert_eq!(
        status,
        StatusCode::CONFLICT,
        "a different run reusing an id is not a retry"
    );

    let request = Request::builder()
        .uri(format!("/v1/runs/{id}"))
        .body(Body::empty())
        .unwrap();

    let (_, body) = call(app, request).await;
    assert_eq!(
        body["run"]["boss"], boss,
        "the stored run must be untouched"
    );
    assert_eq!(body["players"][0]["damage_done"], 100);
}

/// The same body from a different server is a different submission. It used to be
/// accepted as a retry, because the server id was read after the digest was taken.
#[tokio::test]
async fn the_same_body_from_another_server_is_a_conflict() {
    let app = require_db!();
    let id = unique("twoservers");
    let boss = format!("boss-{id}");
    let payload = run(&id, &boss, "kill", 30_000, HASH, "dedicated");

    let from_a = submit(payload.clone(), Some(TOKEN));
    let (status, _) = call(app.clone(), from_a).await;
    assert_eq!(status, StatusCode::CREATED);

    let mut builder = Request::builder()
        .method("POST")
        .uri("/v1/internal/runs")
        .header("content-type", "application/json")
        .header("authorization", format!("Bearer {TOKEN}"));

    builder = builder.header("x-server-id", "review-server-b");
    let from_b = builder.body(Body::from(payload.to_string())).unwrap();

    let (status, _) = call(app, from_b).await;
    assert_eq!(status, StatusCode::CONFLICT);
}

/// There is no compatibility path for a row without a digest, so there must be no
/// way to write one. NOT NULL with no default is the guarantee; this proves the
/// database actually enforces it rather than trusting the migration text.
#[tokio::test]
async fn a_run_cannot_be_stored_without_a_digest() {
    let app = require_db!();
    let mut conn = app.pool.get().await.unwrap();

    let attempt = diesel::sql_query(
        "INSERT INTO runs (id, boss, outcome, duration_ms, content_hash, engine, \
         authority, rankable, worst_overreach_cm, game_server) \
         VALUES ('nodigest0000000000000000000000ab', 'b', 'kill', 1, 'h', 'e', \
         'dedicated', false, 0, 's')",
    )
    .execute(&mut conn)
    .await;

    assert!(
        attempt.is_err(),
        "a row with no digest must be impossible, or the comparison has a hole"
    );
}

#[tokio::test]
async fn malformed_submissions_are_refused_without_storing() {
    let app = require_db!();
    let id = unique("malformed");
    let boss = format!("boss-{id}");

    let mut payload = run(&id, &boss, "kill", 1000, HASH, "dedicated");
    payload["worst_overreach_cm"] = json!(0);
    payload["players"][0]["overreach_cm"] = json!(5000);

    let (status, _) = call(app.clone(), submit(payload, Some(TOKEN))).await;
    assert_eq!(status, StatusCode::UNPROCESSABLE_ENTITY);

    let request = Request::builder()
        .uri(format!("/v1/runs/{id}"))
        .body(Body::empty())
        .unwrap();

    let (status, _) = call(app, request).await;
    assert_eq!(
        status,
        StatusCode::NOT_FOUND,
        "a refused run must not be stored"
    );
}

#[tokio::test]
async fn the_ladder_never_mixes_balance_versions() {
    let app = require_db!();
    let tag = unique("ladder");
    let boss = format!("boss-{tag}");

    // The older patch produces a much faster time, so mixing would be obvious.
    call(
        app.clone(),
        submit(
            run(&unique("cur"), &boss, "kill", 90_000, HASH, "dedicated"),
            Some(TOKEN),
        ),
    )
    .await;
    call(
        app.clone(),
        submit(
            run(
                &unique("old"),
                &boss,
                "kill",
                5_000,
                "hash-previous",
                "dedicated",
            ),
            Some(TOKEN),
        ),
    )
    .await;

    let request = Request::builder()
        .uri(format!("/v1/leaderboards/{boss}"))
        .body(Body::empty())
        .unwrap();

    let (status, body) = call(app.clone(), request).await;
    assert_eq!(status, StatusCode::OK);

    let rows = body.as_array().unwrap();
    assert_eq!(rows.len(), 1, "the default ladder is one balance version");
    assert_eq!(rows[0]["content_hash"], HASH);

    // The older patch still has its own ladder.
    let request = Request::builder()
        .uri(format!(
            "/v1/leaderboards/{boss}?content_hash=hash-previous"
        ))
        .body(Body::empty())
        .unwrap();

    let (_, body) = call(app, request).await;
    let rows = body.as_array().unwrap();
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0]["duration_ms"], 5_000);
}

#[tokio::test]
async fn unranked_runs_are_kept_as_telemetry() {
    let app = require_db!();
    let id = unique("hosted");
    let boss = format!("boss-{id}");

    let (status, body) = call(
        app.clone(),
        submit(
            run(&id, &boss, "kill", 1000, HASH, "player_hosted"),
            Some(TOKEN),
        ),
    )
    .await;

    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["rankable"], false);
    assert_eq!(body["reason"], "not played on a dedicated server");

    let request = Request::builder()
        .uri(format!("/v1/leaderboards/{boss}"))
        .body(Body::empty())
        .unwrap();

    let (_, body) = call(app, request).await;
    assert!(
        body.as_array().unwrap().is_empty(),
        "unranked runs stay off the ladder"
    );
}
