/// Everything the service needs, read once at boot so a missing setting fails
/// loudly at startup rather than on the first request that needed it.
#[derive(Debug, Clone)]
pub struct Config {
    pub database_url: String,
    pub bind_addr: String,

    /// Presented by game servers on /v1/internal/*. Never built into a client.
    pub game_server_token: String,

    /// Balance fingerprints eligible for ranking this season.
    pub ranked_content_hashes: Vec<String>,
}

impl Config {
    pub fn from_env() -> Result<Self, String> {
        Ok(Self {
            database_url: required("DATABASE_URL")?,
            bind_addr: std::env::var("BIND_ADDR").unwrap_or_else(|_| "0.0.0.0:8080".into()),
            game_server_token: required("GAME_SERVER_TOKEN")?,
            ranked_content_hashes: std::env::var("RANKED_CONTENT_HASHES")
                .unwrap_or_default()
                .split(',')
                .map(|hash| hash.trim().to_string())
                .filter(|hash| !hash.is_empty())
                .collect(),
        })
    }
}

fn required(key: &str) -> Result<String, String> {
    std::env::var(key).map_err(|_| format!("{key} must be set"))
}
