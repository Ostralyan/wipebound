use std::time::Duration;

use diesel_async::pooled_connection::bb8::Pool as Bb8Pool;
use diesel_async::pooled_connection::AsyncDieselConnectionManager;
use diesel_async::AsyncPgConnection;

pub type Pool = Bb8Pool<AsyncPgConnection>;

/// Ceiling on connections held by ONE instance of this service.
///
/// Postgres defaults to max_connections = 100 and docker-compose does not raise
/// it, so this is a budget shared with every other instance and with whoever is
/// holding a psql session. Eight leaves room for several backends plus a human.
///
/// It is set explicitly because the previous pool defaulted to cpu_count * 4 --
/// sixty-four on the machine this was written on, and a hundred and twenty-eight
/// on a large cloud instance, which alone would exhaust the server.
const MAX_CONNECTIONS: u32 = 8;

/// One connection is established before this returns, which is what makes a
/// backend that cannot reach its database fail at STARTUP rather than come up
/// and serve errors. A supervisor restarting with backoff is a better answer
/// than a healthy-looking process that cannot do anything.
const MIN_IDLE: u32 = 1;

/// How long a request waits for a free connection before giving up.
///
/// Down from bb8's default of thirty seconds. The game servers submitting runs
/// keep a durable spool and retry with backoff, so a prompt error is handled
/// gracefully and a long hang just occupies one of their request slots doing
/// nothing.
const WAIT_FOR_CONNECTION: Duration = Duration::from_secs(5);

/// Retire connections that have been sitting unused.
///
/// Shorter than bb8's ten-minute default on purpose: cloud load balancers and
/// NAT gateways drop idle TCP connections at around ten minutes, and a socket
/// killed by the network is only discovered by failing a query on the request
/// path. Reaping first means the failure never reaches a caller.
const IDLE_TIMEOUT: Duration = Duration::from_secs(5 * 60);

/// And retire them eventually regardless, so a failover or a rotated credential
/// cannot be papered over by a connection that never gets replaced.
const MAX_LIFETIME: Duration = Duration::from_secs(30 * 60);

pub async fn build_pool(database_url: &str) -> Result<Pool, String> {
    let manager = AsyncDieselConnectionManager::<AsyncPgConnection>::new(database_url);

    Pool::builder()
        .max_size(MAX_CONNECTIONS)
        .min_idle(Some(MIN_IDLE))
        .connection_timeout(WAIT_FOR_CONNECTION)
        .idle_timeout(Some(IDLE_TIMEOUT))
        .max_lifetime(Some(MAX_LIFETIME))
        // test_on_check_out is left at bb8's default of on. It costs a SELECT 1
        // per checkout, which at this traffic is free, and it is the last line of
        // defence for a connection that died between reaping and use.
        .build(manager)
        .await
        .map_err(|error| format!("could not reach the database: {error}"))
}
