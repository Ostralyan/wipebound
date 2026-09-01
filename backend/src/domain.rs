//! What a run record is, and what makes one count.
//!
//! Kept free of Axum and Diesel so the rules can be tested without a server or a
//! database. Every assertion below is a decision about the ladder, not about
//! plumbing, which is exactly the part worth pinning down.

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

pub const SUPPORTED_SCHEMA: i32 = 1;
pub const MAX_PLAYERS: usize = 8;

/// Accepted identity provenances. "steam" is listed before anything produces it
/// so that the day one does, the schema, the digest and the ladder policy are
/// already shaped for it and only the verifier is new.
pub const IDENTITY_ANONYMOUS: &str = "anonymous";
pub const IDENTITY_PROVENANCES: [&str; 2] = [IDENTITY_ANONYMOUS, "steam"];

pub const MAX_NAME_LEN: usize = 24;
pub const MIN_ID_LEN: usize = 8;
pub const MAX_ID_LEN: usize = 64;

/// Whether an id could have come from any provenance we accept. Wider than what
/// the game currently generates, because a platform id is somebody else's format.
pub fn well_formed_id(id: &str) -> bool {
    (MIN_ID_LEN..=MAX_ID_LEN).contains(&id.len())
        && id
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_')
}

/// A name fit to store and print back. Control characters are the point:
/// these strings are rendered in ladders and written to logs.
pub fn well_formed_name(name: &str) -> bool {
    !name.is_empty()
        && name.chars().count() <= MAX_NAME_LEN
        && !name.chars().any(|c| c.is_control())
}

/// Six hours. Long enough that no honest attempt trips it, short enough that a
/// nonsense value cannot sit at the top of a ladder sorted ascending.
pub const MAX_DURATION_MS: i64 = 6 * 60 * 60 * 1_000;

#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct PlayerLine {
    /// How this slot was addressed during the fight. Unique within a run, and
    /// meaningless outside it.
    pub peer: i64,

    /// Who the slot belonged to. Opaque, and its trustworthiness is the
    /// `identity` field's job to state rather than this one's to imply.
    pub player_id: String,
    pub display_name: String,

    /// What the GAME SERVER verified about the id, never what the client
    /// claimed about itself. "anonymous" is an honest admission that nothing was
    /// checked; it identifies an install, not a person.
    pub identity: String,
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

/// A fingerprint of the whole submission, so a duplicate id can be told from a
/// retry.
///
/// LENGTH-PREFIXED, not separator-delimited. An earlier version joined fields with
/// byte 0x1f on the claim that it could not appear in them, which was simply
/// untrue: JSON carries that byte perfectly well, so engine="x<0x1f>y" with
/// content_hash="current" hashed identically to engine="y" with
/// content_hash="current<0x1f>x". Prefixing each field with its length makes the
/// encoding unambiguous whatever the bytes are.
///
/// Takes the submitting server, because a submission is a body AND who sent it.
/// Leaving it out meant the same body from a second server was accepted as a retry
/// of the first, which contradicted the point of having a digest at all.
///
/// Players are sorted, because a roster is a set and two orderings of it are the
/// same run.
pub fn digest(submission: &RunSubmission, game_server: &str) -> String {
    fn field(hasher: &mut Sha256, value: &[u8]) {
        hasher.update((value.len() as u64).to_le_bytes());
        hasher.update(value);
    }

    fn text(hasher: &mut Sha256, value: &str) {
        field(hasher, value.as_bytes());
    }

    fn number(hasher: &mut Sha256, value: i64) {
        field(hasher, &value.to_le_bytes());
    }

    let mut hasher = Sha256::new();

    number(&mut hasher, i64::from(submission.schema));
    text(&mut hasher, &submission.run_id);
    text(&mut hasher, &submission.boss);
    text(&mut hasher, &submission.outcome);
    number(&mut hasher, submission.duration_ms);
    text(&mut hasher, &submission.content_hash);
    text(&mut hasher, &submission.engine);
    text(&mut hasher, &submission.authority);
    number(&mut hasher, submission.worst_overreach_cm);
    text(&mut hasher, game_server);

    let mut roster = submission.players.clone();
    roster.sort_by_key(|player| player.peer);
    number(&mut hasher, roster.len() as i64);

    for player in &roster {
        number(&mut hasher, player.peer);
        text(&mut hasher, &player.player_id);
        text(&mut hasher, &player.display_name);
        text(&mut hasher, &player.identity);
        number(&mut hasher, player.damage_done);
        number(&mut hasher, player.healing_done);
        number(&mut hasher, player.damage_taken);
        number(&mut hasher, player.overreach_cm);
    }

    hex::encode(hasher.finalize())
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
    #[error("player id must be {MIN_ID_LEN}-{MAX_ID_LEN} characters of [A-Za-z0-9_-]")]
    BadPlayerId,
    #[error("display name must be 1-{MAX_NAME_LEN} characters and contain no control characters")]
    BadDisplayName,
    #[error("identity provenance must be one of {IDENTITY_PROVENANCES:?}")]
    BadIdentity,
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

        if !well_formed_id(&player.player_id) {
            return Err(Rejection::BadPlayerId);
        }
        if !well_formed_name(&player.display_name) {
            return Err(Rejection::BadDisplayName);
        }
        if !IDENTITY_PROVENANCES.contains(&player.identity.as_str()) {
            return Err(Rejection::BadIdentity);
        }
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
    require_verified_identity: bool,
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
    // Two slots claiming one VERIFIED identity is somebody playing themselves,
    // and a ladder that ranked it would credit one person for two seats.
    //
    // Deliberately not checked for anonymous ids. An anonymous id is a claim
    // nobody checked, so a collision between two of them establishes nothing --
    // treating it as fraud would be reading meaning into a value that has none.
    // Refusing it would also punish the honest case it cannot tell apart.
    for (index, player) in submission.players.iter().enumerate() {
        if player.identity == IDENTITY_ANONYMOUS {
            continue;
        }
        if submission.players[..index].iter().any(|earlier| {
            earlier.identity == player.identity && earlier.player_id == player.player_id
        }) {
            return Verdict::refused("one player occupied two places in the raid");
        }
    }

    // Off by default, because switching it on today would empty the ladder: no
    // verified provenance exists yet. It is the single line that changes when one
    // does, which is the whole reason provenance is a stored field.
    if require_verified_identity
        && submission
            .players
            .iter()
            .any(|player| player.identity == IDENTITY_ANONYMOUS)
    {
        return Verdict::refused("the ladder requires a verified identity");
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
                player_id: "a1b2c3d4e5f60718".into(),
                display_name: "alice".into(),
                identity: IDENTITY_ANONYMOUS.into(),
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
    fn the_digest_covers_everything_the_submitter_said() {
        const SERVER: &str = "server-a";

        // A submission is a body AND who sent it. Without this the same body from
        // a second server was accepted as a retry of the first.
        assert_ne!(
            digest(&honest(), "server-a"),
            digest(&honest(), "server-b"),
            "the same body from another server is not the same submission"
        );

        // The separator-delimited version hashed these identically, because JSON
        // carries the separator byte perfectly well.
        let mut shifted_left = honest();
        shifted_left.content_hash = "current".into();
        shifted_left.engine = "x\u{1f}y".into();

        let mut shifted_right = honest();
        shifted_right.content_hash = "current\u{1f}x".into();
        shifted_right.engine = "y".into();

        assert_ne!(
            digest(&shifted_left, SERVER),
            digest(&shifted_right, SERVER),
            "field boundaries survive a control character inside a field"
        );

        let original = honest();
        assert_eq!(
            digest(&original, SERVER),
            digest(&honest(), SERVER),
            "the same run digests the same"
        );

        // Every one of these was invisible to the old five-field comparison.
        let mut other_boss = honest();
        other_boss.boss = "Something Else".into();
        assert_ne!(digest(&original, SERVER), digest(&other_boss, SERVER));

        let mut other_engine = honest();
        other_engine.engine = "4.9.9".into();
        assert_ne!(digest(&original, SERVER), digest(&other_engine, SERVER));

        let mut other_damage = honest();
        other_damage.players[0].damage_done += 1;
        assert_ne!(digest(&original, SERVER), digest(&other_damage, SERVER));

        let mut other_peer = honest();
        other_peer.players[0].peer += 1;
        assert_ne!(digest(&original, SERVER), digest(&other_peer, SERVER));

        // The roster is a set: the same players in another order are the same run.
        let mut two = honest();
        two.players.push(PlayerLine {
            peer: 5,
            player_id: "f0e1d2c3b4a59687".into(),
            display_name: "bob".into(),
            identity: IDENTITY_ANONYMOUS.into(),
            damage_done: 7,
            healing_done: 0,
            damage_taken: 0,
            overreach_cm: 0,
        });

        let mut reversed = two.clone();
        reversed.players.reverse();
        assert_eq!(
            digest(&two, SERVER),
            digest(&reversed, SERVER),
            "roster order is not identity"
        );
    }

    #[test]
    fn an_honest_clear_is_ranked() {
        let run = honest();
        assert_eq!(check(&run), Ok(()));
        assert_eq!(
            rank(&run, &season(), ALLOWED_OVERREACH, false),
            Verdict::ranked()
        );
    }

    #[test]
    fn a_wipe_is_kept_but_not_ranked() {
        let mut run = honest();
        run.outcome = "wipe".into();
        assert_eq!(check(&run), Ok(()));
        assert!(!rank(&run, &season(), ALLOWED_OVERREACH, false).rankable);
    }

    #[test]
    fn identity_is_validated_like_every_other_untrusted_field() {
        let mut short = honest();
        short.players[0].player_id = "abc".into();
        assert_eq!(check(&short), Err(Rejection::BadPlayerId));

        let mut punctuated = honest();
        punctuated.players[0].player_id = "not a valid id!!".into();
        assert_eq!(check(&punctuated), Err(Rejection::BadPlayerId));

        // The one that matters. These names are written to the game server's log,
        // which is read line by line by people and by tools/latency-test.sh, so a
        // name carrying a newline could forge log lines and claim anything.
        let mut forged = honest();
        forged.players[0].display_name = "alice\n[resolve] boss died".into();
        assert_eq!(check(&forged), Err(Rejection::BadDisplayName));

        let mut empty = honest();
        empty.players[0].display_name = String::new();
        assert_eq!(check(&empty), Err(Rejection::BadDisplayName));

        let mut long = honest();
        long.players[0].display_name = "x".repeat(MAX_NAME_LEN + 1);
        assert_eq!(check(&long), Err(Rejection::BadDisplayName));

        // Provenance is a closed set. A client inventing one would be inventing
        // its own trustworthiness.
        let mut invented = honest();
        invented.players[0].identity = "verified-honestly".into();
        assert_eq!(check(&invented), Err(Rejection::BadIdentity));

        // A name of exactly the limit, and a plausible platform id, both pass.
        let mut edge = honest();
        edge.players[0].display_name = "x".repeat(MAX_NAME_LEN);
        edge.players[0].player_id = "76561198000000000".into();
        edge.players[0].identity = "steam".into();
        assert_eq!(check(&edge), Ok(()));
    }

    #[test]
    fn identity_is_part_of_what_makes_a_run_that_run() {
        const SERVER: &str = "server-a";
        let original = honest();

        // Otherwise two different people's runs could collide as retries of each
        // other, which is exactly what the digest exists to prevent.
        let mut renamed = honest();
        renamed.players[0].display_name = "mallory".into();
        assert_ne!(digest(&original, SERVER), digest(&renamed, SERVER));

        let mut reassigned = honest();
        reassigned.players[0].player_id = "0000111122223333".into();
        assert_ne!(digest(&original, SERVER), digest(&reassigned, SERVER));

        let mut promoted = honest();
        promoted.players[0].identity = "steam".into();
        assert_ne!(digest(&original, SERVER), digest(&promoted, SERVER));
    }

    #[test]
    fn one_person_may_not_occupy_two_seats_once_identity_is_verified() {
        let mut twice = honest();
        let mut clone = twice.players[0].clone();
        clone.peer += 1;
        twice.players.push(clone);
        twice.worst_overreach_cm = 0;

        // Anonymous is an install, not a person: three clients on one machine
        // share one, which is how this project's own tests run.
        assert!(rank(&twice, &season(), ALLOWED_OVERREACH, false).rankable);

        for player in &mut twice.players {
            player.identity = "steam".into();
        }
        assert_eq!(
            rank(&twice, &season(), ALLOWED_OVERREACH, false).reason,
            Some("one player occupied two places in the raid")
        );
    }

    #[test]
    fn requiring_verified_identity_is_one_flag_and_changes_nothing_else() {
        let run = honest();

        // Off, which it must be today: nothing produces a verified provenance,
        // so switching it on would empty the ladder.
        assert!(rank(&run, &season(), ALLOWED_OVERREACH, false).rankable);

        assert_eq!(
            rank(&run, &season(), ALLOWED_OVERREACH, true).reason,
            Some("the ladder requires a verified identity")
        );

        // And the same run with a verified provenance passes the stricter policy,
        // so the flag is the whole migration.
        let mut verified = honest();
        verified.players[0].identity = "steam".into();
        assert!(rank(&verified, &season(), ALLOWED_OVERREACH, true).rankable);
    }

    #[test]
    fn a_player_hosted_run_can_never_rank() {
        let mut run = honest();
        run.authority = "player_hosted".into();
        assert_eq!(
            rank(&run, &season(), ALLOWED_OVERREACH, false).reason,
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
        assert!(rank(&run, &season(), ALLOWED_OVERREACH, false).rankable);
    }

    #[test]
    fn overreach_disqualifies() {
        let mut run = honest();
        run.players[0].overreach_cm = 1_045_780;
        run.worst_overreach_cm = 1_045_780;
        assert_eq!(check(&run), Ok(()));
        assert!(!rank(&run, &season(), ALLOWED_OVERREACH, false).rankable);
    }

    #[test]
    fn a_run_from_another_balance_patch_does_not_pollute_the_ladder() {
        let mut run = honest();
        run.content_hash = "0000000000000000".into();
        assert!(!rank(&run, &season(), ALLOWED_OVERREACH, false).rankable);
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
