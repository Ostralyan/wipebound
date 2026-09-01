use diesel_async::pooled_connection::deadpool::Pool as DeadpoolPool;
use diesel_async::pooled_connection::AsyncDieselConnectionManager;
use diesel_async::AsyncPgConnection;

pub type Pool = DeadpoolPool<AsyncPgConnection>;

pub fn build_pool(database_url: &str) -> Result<Pool, String> {
    let manager = AsyncDieselConnectionManager::<AsyncPgConnection>::new(database_url);
    Pool::builder(manager).build().map_err(|error| error.to_string())
}
