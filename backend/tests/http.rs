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

async fn state() -> Option<AppState> {
    let database_url = std::env::var("DATABASE_URL").ok()?;

    let config = Config {
        database_url: database_url.clone(),
        bind_addr: "127.0.0.1:0".into(),
        game_server_token: TOKEN.into(),
        ranked_content_hashes: vec![HASH.into(), "hash-previous".into()],
        current_content_hash: HASH.into(),
        ranked_max_overreach_cm: 200,
        ranked_require_verified_identity: false,
        log_retention_days: 90,
    };

    Some(AppState {
        pool: db::build_pool(&database_url).await.ok()?,
        config: Arc::new(config),
    })
}

async fn call_raw(state: AppState, request: Request<Body>) -> (StatusCode, Vec<u8>) {
    let response = router(state).oneshot(request).await.expect("router failed");
    let status = response.status();
    let bytes = http_body_util::BodyExt::collect(response.into_body())
        .await
        .expect("body")
        .to_bytes();

    (status, bytes.to_vec())
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
        "schema": 2,
        "run_id": id,
        "boss": boss,
        "outcome": outcome,
        "duration_ms": duration_ms,
        "content_hash": hash,
        "engine": "test",
        "authority": authority,
        "worst_overreach_cm": 0,
        "players": [{
            "peer": 1,
            "player_id": "a1b2c3d4e5f60718", "display_name": "alice",
            "identity": "anonymous",
            "damage_done": 100, "healing_done": 0,
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
        match state().await {
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
async fn the_ladder_can_say_who_played() {
    // The point of the whole exercise. A ladder keyed on ENet peer ids could rank
    // a run and never attribute it: the id is a fresh random integer every
    // connection, so two runs by one person shared no key and no row could be
    // read back as "this person did this".
    let app = require_db!();
    let id = unique("ladder");
    let boss = unique("Nameable");

    let (status, _) = call(
        app.clone(),
        submit(
            run(&id, &boss, "kill", 42_000, HASH, "dedicated"),
            Some(TOKEN),
        ),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);

    let request = Request::builder()
        .uri(format!("/v1/leaderboards/{boss}?content_hash={HASH}"))
        .body(Body::empty())
        .unwrap();

    let (status, body) = call(app, request).await;
    assert_eq!(status, StatusCode::OK);

    let entries = body.as_array().expect("a ladder is a list");
    assert_eq!(entries.len(), 1);
    assert_eq!(entries[0]["duration_ms"], 42_000);
    assert_eq!(entries[0]["players"][0]["display_name"], "alice");

    // And it says how much that name is worth, rather than implying it is a fact.
    assert_eq!(entries[0]["players"][0]["identity"], "anonymous");

    // The opaque id is not handed out: a ladder shows who played, it does not
    // distribute a key that identifies them somewhere else.
    assert!(entries[0]["players"][0]["player_id"].is_null());
}

/// A well-formed document for one run: alice hits twice, is caught 1.8m inside a
/// Crater, and dies at nine seconds of thirty.
fn combat_log(run_id: &str, boss: &str) -> Value {
    serde_json::json!({
        "format": 2, "run_id": run_id, "boss": boss,
        "duration_ms": 30000, "truncated": false,
        "actors": [
            {"id": 1, "name": "alice", "kind": "hero", "class": "Ember", "player_id": "a1b2c3d4e5f60718"},
            {"id": -1, "name": "boss", "kind": "boss", "class": "", "player_id": ""}
        ],
        "names": ["Lance", "Crater"],
        "events": [
            [0,    10, 1, 1,  -1, 0,  0,    0],
            [1000, 0,  1, -1, 0,  40, 0,    0],
            [2000, 0,  1, -1, 0,  60, 0,    0],
            [3000, 4,  -1, 1, 1,  0,  -180, 1],
            [3010, 0,  -1, 1, 1,  25, 0,    0],
            [9000, 9,  -1, 1, 1,  0,  0,    0]
        ]
    })
}

fn post_log(run_id: &str, body: String) -> Request<Body> {
    Request::builder()
        .method("POST")
        .uri(format!("/v1/internal/runs/{run_id}/log"))
        .header("content-type", "application/json")
        .header("authorization", format!("Bearer {TOKEN}"))
        .body(Body::from(body))
        .unwrap()
}

#[tokio::test]
async fn a_combat_log_becomes_numbers_a_site_can_show() {
    let app = require_db!();
    let id = unique("logged");
    let boss = unique("Logged");

    let (status, _) = call(
        app.clone(),
        submit(
            run(&id, &boss, "kill", 30_000, HASH, "dedicated"),
            Some(TOKEN),
        ),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);

    // alice hits twice, is caught 1.8m inside a Crater, and dies. Her id is the
    // run's peer, because a log whose roster disagrees with its run is not
    // evidence for it.
    let log = combat_log(&id, &boss).to_string();

    let (status, body) = call(app.clone(), post_log(&id, log)).await;
    assert_eq!(
        status,
        StatusCode::CREATED,
        "an uncompressed log is accepted too"
    );
    assert_eq!(body["events"], 6);

    let request = Request::builder()
        .uri(format!("/v1/runs/{id}"))
        .body(Body::empty())
        .unwrap();

    let (status, body) = call(app.clone(), request).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["has_log"], true);

    let alice = &body["stats"][0];
    assert_eq!(alice["display_name"], "alice");
    assert_eq!(alice["damage_done"], 100);
    assert_eq!(alice["damage_taken"], 25);

    // The measurement the whole Judged event exists for.
    assert_eq!(alice["avoidable_damage"], 25);

    // And a per-second figure is divided by time on her feet, not by the fight.
    assert_eq!(alice["alive_ms"], 9000);

    // THE DOWNLOAD MUST DECODE. An uncompressed upload used to be stored raw and
    // served with Content-Encoding: gzip, so the status was 200 and no browser
    // could read a byte of it. Checking the status alone is what let that pass.
    let request = Request::builder()
        .uri(format!("/v1/runs/{id}/log"))
        .body(Body::empty())
        .unwrap();

    let (status, bytes) = call_raw(app.clone(), request).await;
    assert_eq!(status, StatusCode::OK);

    let mut json = Vec::new();
    std::io::Read::read_to_end(&mut flate2::read::GzDecoder::new(&bytes[..]), &mut json)
        .expect("what is served as gzip must be gzip");

    let round_tripped: Value = serde_json::from_slice(&json).expect("and must be the document");
    assert_eq!(round_tripped["run_id"], id);
}

#[tokio::test]
async fn a_log_is_evidence_and_therefore_not_rewritable() {
    let app = require_db!();
    let id = unique("evidence");
    let boss = unique("Evidence");

    let (status, _) = call(
        app.clone(),
        submit(
            run(&id, &boss, "kill", 30_000, HASH, "dedicated"),
            Some(TOKEN),
        ),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);

    let log = combat_log(&id, &boss);
    let (status, _) = call(app.clone(), post_log(&id, log.to_string())).await;
    assert_eq!(status, StatusCode::CREATED);

    // The same evidence twice is a retry, not a second upload.
    let (status, body) = call(app.clone(), post_log(&id, log.to_string())).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["duplicate"], true);

    // DIFFERENT evidence for the same run is refused. Uploading used to delete
    // whatever was there along with every statistic derived from it, so any
    // authenticated server could rewrite the meters behind a run.
    let mut altered = combat_log(&id, &boss);
    altered["events"][1][5] = serde_json::json!(9_000);

    let (status, _) = call(app.clone(), post_log(&id, altered.to_string())).await;
    assert_eq!(status, StatusCode::CONFLICT);

    // And the original numbers are untouched.
    let request = Request::builder()
        .uri(format!("/v1/runs/{id}"))
        .body(Body::empty())
        .unwrap();
    let (_, body) = call(app, request).await;
    assert_eq!(body["stats"][0]["damage_done"], 100);
}

#[tokio::test]
async fn a_log_for_another_run_is_refused() {
    let app = require_db!();
    let id = unique("mine");
    let boss = unique("Mine");

    call(
        app.clone(),
        submit(
            run(&id, &boss, "kill", 30_000, HASH, "dedicated"),
            Some(TOKEN),
        ),
    )
    .await;

    // Says it belongs to somebody else's run.
    let (status, _) = call(
        app.clone(),
        post_log(
            &id,
            combat_log("00000000000000000000000000000000", &boss).to_string(),
        ),
    )
    .await;
    assert_eq!(
        status,
        StatusCode::UNPROCESSABLE_ENTITY,
        "a log names its own run"
    );

    // Right run, different fight.
    let (status, _) = call(
        app.clone(),
        post_log(&id, combat_log(&id, "A Different Boss").to_string()),
    )
    .await;
    assert_eq!(
        status,
        StatusCode::UNPROCESSABLE_ENTITY,
        "and must describe that run"
    );

    // Right run, wrong people.
    let mut strangers = combat_log(&id, &boss);
    strangers["actors"][0]["id"] = serde_json::json!(4242);
    for event in strangers["events"].as_array_mut().unwrap() {
        for slot in [2usize, 3usize] {
            if event[slot] == 1 {
                event[slot] = serde_json::json!(4242);
            }
        }
    }

    let (status, _) = call(app, post_log(&id, strangers.to_string())).await;
    assert_eq!(
        status,
        StatusCode::UNPROCESSABLE_ENTITY,
        "and that run's players"
    );
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
