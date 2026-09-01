/// Everything the service needs, read once at boot so a missing setting fails
/// loudly at startup rather than on the first request that needed it.
#[derive(Debug, Clone)]
pub struct Config {
    pub database_url: String,
    pub bind_addr: String,

    /// Presented by game servers on /v1/internal/*. Never built into a client.
    pub game_server_token: String,

    /// Balance fingerprints eligible for ranking at all. More than one so a deploy
    /// can overlap; a run on any of them may be stored as ranked.
    pub ranked_content_hashes: Vec<String>,

    /// The single fingerprint the default ladder is FOR.
    ///
    /// Separate from the set above on purpose. Defaulting a ladder to "every hash
    /// we accept" produced one duration-sorted list spanning incompatible balance
    /// numbers, which is worse than no ladder because it looks like one.
    pub current_content_hash: String,

    /// How much position overreach a run may carry and still be ranked.
    ///
    /// Not zero, and configurable rather than baked in. Honest play produces a
    /// little: a status change takes an interval to replicate, and for that window
    /// a client is legitimately moving at a speed the server has already stopped
    /// believing in. Where the line sits is a decision for whoever runs the ladder,
    /// not a constant in a validator.
    pub ranked_max_overreach_cm: i64,
}

impl Config {
    pub fn from_env() -> Result<Self, String> {
        let ranked: Vec<String> = std::env::var("RANKED_CONTENT_HASHES")
            .unwrap_or_default()
            .split(',')
            .map(|hash| hash.trim().to_string())
            .filter(|hash| !hash.is_empty())
            .collect();

        Ok(Self {
            database_url: required("DATABASE_URL")?,
            bind_addr: std::env::var("BIND_ADDR").unwrap_or_else(|_| "0.0.0.0:8080".into()),
            game_server_token: required("GAME_SERVER_TOKEN")?,
            ranked_content_hashes: ranked.clone(),
            current_content_hash: std::env::var("CURRENT_CONTENT_HASH")
                .ok()
                .filter(|hash| !hash.trim().is_empty())
                .unwrap_or_else(|| ranked.first().cloned().unwrap_or_default()),
            ranked_max_overreach_cm: std::env::var("RANKED_MAX_OVERREACH_CM")
                .ok()
                .and_then(|value| value.parse().ok())
                .unwrap_or(200),
        })
    }
}

fn required(key: &str) -> Result<String, String> {
    std::env::var(key).map_err(|_| format!("{key} must be set"))
}
