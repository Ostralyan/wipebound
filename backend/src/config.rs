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

    /// Whether a run needs identities the server actually verified.
    ///
    /// Off by default, and it has to be: nothing produces a verified provenance
    /// yet, so switching it on today empties the ladder. It exists switched off
    /// because that is the point of storing provenance rather than assuming it --
    /// the day a platform ticket can be checked, this flag is the whole migration.
    pub ranked_require_verified_identity: bool,

    /// How long a replay stays available, in days. Zero keeps them for ever.
    ///
    /// Only the log expires; the numbers derived from it are permanent. A run
    /// three months old keeps its meters and its ladder position and loses the
    /// ability to be watched.
    pub log_retention_days: i64,
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
            ranked_require_verified_identity: flag("RANKED_REQUIRE_VERIFIED_IDENTITY", false)?,
            log_retention_days: std::env::var("LOG_RETENTION_DAYS")
                .ok()
                .and_then(|value| value.parse().ok())
                .unwrap_or(90),
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

/// A boolean from the environment, or a refusal to start.
///
/// Anything unrecognised is an ERROR rather than a default. This one used to
/// treat every value except "1" and "true" as false, so an operator who wrote
/// RANKED_REQUIRE_VERIFIED_IDENTITY=treu got the permissive policy and no
/// indication of it. A security control that silently turns itself off when
/// misspelled is worse than not having the switch.
fn flag(key: &str, fallback: bool) -> Result<bool, String> {
    let Ok(raw) = std::env::var(key) else {
        return Ok(fallback);
    };

    match raw.trim().to_ascii_lowercase().as_str() {
        "1" | "true" | "yes" | "on" => Ok(true),
        "0" | "false" | "no" | "off" => Ok(false),
        other => Err(format!(
            "{key} must be true or false, not {other:?} -- refusing to guess at a security setting"
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::flag;

    /// Uses its own key names so it cannot collide with a real environment.
    #[test]
    fn a_misspelled_flag_refuses_to_start_rather_than_defaulting() {
        std::env::set_var("WB_TEST_FLAG", "treu");
        assert!(
            flag("WB_TEST_FLAG", false).is_err(),
            "a typo must not be silently false"
        );

        for yes in ["1", "true", "TRUE", " on ", "yes"] {
            std::env::set_var("WB_TEST_FLAG", yes);
            assert_eq!(flag("WB_TEST_FLAG", false), Ok(true), "{yes:?} means true");
        }

        for no in ["0", "false", "OFF", "no"] {
            std::env::set_var("WB_TEST_FLAG", no);
            assert_eq!(flag("WB_TEST_FLAG", true), Ok(false), "{no:?} means false");
        }

        std::env::remove_var("WB_TEST_FLAG");
        assert_eq!(flag("WB_TEST_FLAG", true), Ok(true), "unset falls back");
    }
}
