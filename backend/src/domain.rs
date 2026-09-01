//! What a run record is, and what makes one count.
//!
//! Kept free of Axum and Diesel so the rules can be tested without a server or a
//! database. Every assertion below is a decision about the ladder, not about
//! plumbing, which is exactly the part worth pinning down.

use serde::{Deserialize, Serialize};

pub const SUPPORTED_SCHEMA: i32 = 1;
pub const MAX_PLAYERS: usize = 8;

/// Six hours. Long enough that no honest attempt trips it, short enough that a
/// nonsense value cannot sit at the top of a ladder sorted ascending.
pub const MAX_DURATION_MS: i64 = 6 * 60 * 60 * 1_000;

#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct PlayerLine {
    pub peer: i64,
    pub damage_done: i64,
    pub healing_done: i64,
    pub damage_taken: i64,
    pub overreach_cm: i64,
}

/// Exactly what a game server posts. Note the absence of a `rankable` field: the
/// submitter reports facts and this crate draws the conclusion.
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct RunSubmission {
    pub schema: i32,
    pub run_id: String,
    pub boss: String,
    pub outcome: String,
    pub duration_ms: i64,
    pub content_hash: String,
    pub engine: String,
    pub authority: String,
    pub worst_overreach_cm: i64,
    pub players: Vec<PlayerLine>,
}

/// Malformed or self-contradictory. Refused outright, nothing stored.
#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum Rejection {
    #[error("unsupported schema {0}")]
    UnsupportedSchema(i32),
    #[error("run id must be 32 lowercase hex characters")]
    BadRunId,
    #[error("boss name is missing or too long")]
    BadBoss,
    #[error("outcome must be kill or wipe")]
    BadOutcome,
    #[error("authority must be dedicated or player_hosted")]
    BadAuthority,
    #[error("duration out of range")]
    BadDuration,
    #[error("a run needs between 1 and {MAX_PLAYERS} players")]
    BadRoster,
    #[error("duplicate peer {0}")]
    DuplicatePeer(i64),
    #[error("negative quantity")]
    NegativeQuantity,
    #[error("worst_overreach_cm disagrees with the per-player figures")]
    InconsistentOverreach,
}

/// Well-formed. Stored either way; `rankable` decides whether it appears on a
/// ladder, and the reason is kept so an unranked run can explain itself.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Verdict {
    pub rankable: bool,
    pub reason: Option<&'static str>,
}

impl Verdict {
    fn ranked() -> Self {
        Self {
            rankable: true,
            reason: None,
        }
    }

    fn refused(reason: &'static str) -> Self {
        Self {
            rankable: false,
            reason: Some(reason),
        }
    }
}

fn is_hex32(value: &str) -> bool {
    value.len() == 32
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

/// Structural checks. Anything that fails here is malformed or lying about
/// itself, and is not worth keeping.
pub fn check(submission: &RunSubmission) -> Result<(), Rejection> {
    if submission.schema != SUPPORTED_SCHEMA {
        return Err(Rejection::UnsupportedSchema(submission.schema));
    }
    if !is_hex32(&submission.run_id) {
        return Err(Rejection::BadRunId);
    }
    if submission.boss.is_empty() || submission.boss.len() > 64 {
        return Err(Rejection::BadBoss);
    }
    if !matches!(submission.outcome.as_str(), "kill" | "wipe") {
        return Err(Rejection::BadOutcome);
    }
    if !matches!(submission.authority.as_str(), "dedicated" | "player_hosted") {
        return Err(Rejection::BadAuthority);
    }
    if submission.duration_ms <= 0 || submission.duration_ms > MAX_DURATION_MS {
        return Err(Rejection::BadDuration);
    }
    if submission.players.is_empty() || submission.players.len() > MAX_PLAYERS {
        return Err(Rejection::BadRoster);
    }
    if submission.worst_overreach_cm < 0 {
        return Err(Rejection::NegativeQuantity);
    }

    let mut seen: Vec<i64> = Vec::with_capacity(submission.players.len());
    let mut worst = 0i64;

    for player in &submission.players {
        if player.damage_done < 0
            || player.healing_done < 0
            || player.damage_taken < 0
            || player.overreach_cm < 0
        {
            return Err(Rejection::NegativeQuantity);
        }
        if seen.contains(&player.peer) {
            return Err(Rejection::DuplicatePeer(player.peer));
        }
        seen.push(player.peer);
        worst = worst.max(player.overreach_cm);
    }

    // The submitter reports the summary AND the parts. If they disagree, the
    // record has been edited after the fact, and the summary is the field a
    // tamperer would reach for first.
    if submission.worst_overreach_cm != worst {
        return Err(Rejection::InconsistentOverreach);
    }

    Ok(())
}

/// Whether a well-formed run counts. Order matters only for which reason is
/// reported, and the order is chosen so the most fundamental problem wins.
pub fn rank(
    submission: &RunSubmission,
    ranked_hashes: &[String],
    max_overreach_cm: i64,
) -> Verdict {
    if submission.authority != "dedicated" {
        // Whoever hosts is the authority and can forge every number above.
        return Verdict::refused("not played on a dedicated server");
    }
    if !ranked_hashes
        .iter()
        .any(|hash| hash == &submission.content_hash)
    {
        return Verdict::refused("balance data is not part of the current season");
    }
    if submission.worst_overreach_cm > max_overreach_cm {
        return Verdict::refused("a client claimed positions it could not have reached");
    }
    if submission.outcome != "kill" {
        return Verdict::refused("the boss survived");
    }

    Verdict::ranked()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn honest() -> RunSubmission {
        RunSubmission {
            schema: SUPPORTED_SCHEMA,
            run_id: "b8bd26e9aa2e8e42964fba0e43d50867".into(),
            boss: "The Wipebringer".into(),
            outcome: "kill".into(),
            duration_ms: 254_300,
            content_hash: "9cf1e05383cda8ec".into(),
            engine: "4.7.2-stable (official)".into(),
            authority: "dedicated".into(),
            worst_overreach_cm: 0,
            players: vec![PlayerLine {
                peer: 817_129_303,
                damage_done: 4210,
                healing_done: 0,
                damage_taken: 890,
                overreach_cm: 0,
            }],
        }
    }

    fn season() -> Vec<String> {
        vec!["9cf1e05383cda8ec".to_string()]
    }

    const ALLOWED_OVERREACH: i64 = 200;

    #[test]
    fn an_honest_clear_is_ranked() {
        let run = honest();
        assert_eq!(check(&run), Ok(()));
        assert_eq!(rank(&run, &season(), ALLOWED_OVERREACH), Verdict::ranked());
    }

    #[test]
    fn a_wipe_is_kept_but_not_ranked() {
        let mut run = honest();
        run.outcome = "wipe".into();
        assert_eq!(check(&run), Ok(()));
        assert!(!rank(&run, &season(), ALLOWED_OVERREACH).rankable);
    }

    #[test]
    fn a_player_hosted_run_can_never_rank() {
        let mut run = honest();
        run.authority = "player_hosted".into();
        assert_eq!(
            rank(&run, &season(), ALLOWED_OVERREACH).reason,
            Some("not played on a dedicated server")
        );
    }

    #[test]
    fn a_little_overreach_is_tolerated() {
        // Honest play produces some: a slow takes an interval to replicate, and for
        // that window the client is legitimately faster than the server believes.
        let mut run = honest();
        run.players[0].overreach_cm = 40;
        run.worst_overreach_cm = 40;
        assert!(rank(&run, &season(), ALLOWED_OVERREACH).rankable);
    }

    #[test]
    fn overreach_disqualifies() {
        let mut run = honest();
        run.players[0].overreach_cm = 1_045_780;
        run.worst_overreach_cm = 1_045_780;
        assert_eq!(check(&run), Ok(()));
        assert!(!rank(&run, &season(), ALLOWED_OVERREACH).rankable);
    }

    #[test]
    fn a_run_from_another_balance_patch_does_not_pollute_the_ladder() {
        let mut run = honest();
        run.content_hash = "0000000000000000".into();
        assert!(!rank(&run, &season(), ALLOWED_OVERREACH).rankable);
    }

    #[test]
    fn a_summary_that_contradicts_its_own_parts_is_refused() {
        // The tamperer's first move: zero the headline and leave the detail behind.
        let mut run = honest();
        run.players[0].overreach_cm = 5_000;
        run.worst_overreach_cm = 0;
        assert_eq!(check(&run), Err(Rejection::InconsistentOverreach));
    }

    #[test]
    fn malformed_submissions_are_refused_rather_than_stored() {
        let mut wrong_schema = honest();
        wrong_schema.schema = 99;
        assert_eq!(check(&wrong_schema), Err(Rejection::UnsupportedSchema(99)));

        let mut bad_id = honest();
        bad_id.run_id = "not-hex".into();
        assert_eq!(check(&bad_id), Err(Rejection::BadRunId));

        let mut uppercase_id = honest();
        uppercase_id.run_id = "B8BD26E9AA2E8E42964FBA0E43D50867".into();
        assert_eq!(check(&uppercase_id), Err(Rejection::BadRunId));

        let mut instant = honest();
        instant.duration_ms = 0;
        assert_eq!(check(&instant), Err(Rejection::BadDuration));

        let mut eternal = honest();
        eternal.duration_ms = MAX_DURATION_MS + 1;
        assert_eq!(check(&eternal), Err(Rejection::BadDuration));

        let mut empty = honest();
        empty.players.clear();
        assert_eq!(check(&empty), Err(Rejection::BadRoster));

        let mut negative = honest();
        negative.players[0].damage_done = -1;
        assert_eq!(check(&negative), Err(Rejection::NegativeQuantity));
    }

    #[test]
    fn the_same_player_cannot_appear_twice() {
        let mut run = honest();
        let clone = run.players[0].clone();
        run.players.push(clone);
        assert_eq!(check(&run), Err(Rejection::DuplicatePeer(817_129_303)));
    }
}
