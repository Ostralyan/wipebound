//! Reading a combat log, and working out what it says.
//!
//! Derived ONCE, when the log arrives, rather than by parsing a blob on every
//! page view. A run is written once and read for as long as the ladder lives, so
//! the expensive direction is the one that should happen least.
//!
//! Pure: no database, no HTTP. The interesting part is the arithmetic and the
//! arithmetic is where mistakes hide, so it is reachable by tests without either.

use std::collections::HashMap;

use serde::Deserialize;

/// Event types, matching CombatLog.LogEventType in the game.
const DAMAGE: i64 = 0;
const HEAL: i64 = 1;
const CAST_START: i64 = 2;
const JUDGED: i64 = 4;
const INTERRUPT: i64 = 7;
const DISPEL: i64 = 8;
const DEATH: i64 = 9;
const SPAWN: i64 = 10;
const RESOURCE_SPENT: i64 = 11;

/// How close in time a damage event has to be to a judgement for the two to be
/// the same moment.
///
/// They are logged microseconds apart, from two separate reads of the clock, so
/// they round to the same millisecond nearly always and to adjacent ones
/// sometimes. A window rather than equality, and small enough that two different
/// casts of the same ability on the same target cannot be confused.
const SAME_MOMENT_MS: i64 = 150;

#[derive(Debug, Deserialize)]
pub struct Actor {
    pub id: i64,
    pub name: String,
    pub kind: String,
    #[serde(default)]
    pub class: String,
    #[serde(default)]
    pub player_id: String,
}

#[derive(Debug, Deserialize)]
pub struct Document {
    pub format: i32,
    pub duration_ms: i64,
    #[serde(default)]
    pub truncated: bool,
    pub actors: Vec<Actor>,
    /// Every name the events refer to: abilities, statuses and phases alike.
    #[serde(alias = "abilities")]
    pub names: Vec<String>,

    /// Rows of [t_ms, type, source, target, ability, amount, a, b].
    pub events: Vec<Vec<i64>>,
}

#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub struct PlayerStats {
    pub combat_id: i64,
    pub player_id: String,
    pub display_name: String,
    pub class_name: String,
    pub damage_done: i64,
    pub healing_done: i64,
    pub overhealing: i64,
    pub damage_taken: i64,
    pub damage_absorbed: i64,
    pub avoidable_damage: i64,
    pub interrupts: i32,
    pub dispels: i32,
    pub deaths: i32,
    pub alive_ms: i64,

    /// What they spent to do all of it. Half of a rotation is knowing whether
    /// somebody ran dry.
    pub resource_spent: i64,
}

#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub struct AbilityStats {
    pub combat_id: i64,
    pub ability: String,
    pub damage: i64,
    pub healing: i64,
    pub hits: i32,
    pub casts: i32,
    pub resource_spent: i64,
}

#[derive(Debug)]
pub struct Derived {
    pub format: i32,
    pub events: i32,
    pub truncated: bool,
    pub players: Vec<PlayerStats>,
    pub abilities: Vec<AbilityStats>,
}

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum LogError {
    #[error("not a combat log: {0}")]
    Unreadable(String),
    #[error("combat log format {0} is not understood")]
    UnsupportedFormat(i32),
    #[error("combat log is for a different run")]
    WrongRun,
}

/// The format this backend can read. Bumped with the game's CombatLog.FormatVersion.
///
/// 2 added mana to the tracks and renamed the string table, which changes how a
/// reader keys the lanes -- so a version 1 document is refused rather than
/// half-understood. Logs already stored stay readable: they are served back as
/// the bytes they arrived as, and the site sizes its stride from the document.
pub const SUPPORTED_FORMAT: i32 = 2;

pub fn parse(json: &[u8]) -> Result<Document, LogError> {
    let document: Document =
        serde_json::from_slice(json).map_err(|error| LogError::Unreadable(error.to_string()))?;

    if document.format != SUPPORTED_FORMAT {
        return Err(LogError::UnsupportedFormat(document.format));
    }

    Ok(document)
}

/// Fold the event stream into the numbers a site shows.
pub fn derive(document: &Document) -> Derived {
    let mut players: HashMap<i64, PlayerStats> = HashMap::new();
    let mut abilities: HashMap<(i64, String), AbilityStats> = HashMap::new();

    // Only heroes get a row. A boss's damage done is the fight, not a
    // contribution, and a minion's is the boss's.
    for actor in &document.actors {
        if actor.kind != "hero" {
            continue;
        }

        players.insert(
            actor.id,
            PlayerStats {
                combat_id: actor.id,
                player_id: actor.player_id.clone(),
                display_name: actor.name.clone(),
                class_name: actor.class.clone(),
                alive_ms: document.duration_ms,
                ..Default::default()
            },
        );
    }

    // Where somebody was judged to be standing inside a telegraph, so damage
    // from that ability at that moment can be called avoidable.
    let mut caught: HashMap<(i64, i64), Vec<i64>> = HashMap::new();
    for row in &document.events {
        if row.len() < 8 || row[1] != JUDGED || row[7] != 1 {
            continue;
        }
        caught.entry((row[3], row[4])).or_default().push(row[0]);
    }

    let mut spawned: HashMap<i64, i64> = HashMap::new();

    for row in &document.events {
        if row.len() < 8 {
            continue;
        }

        let (at, kind, source, target, ability, amount, a, _b) = (
            row[0], row[1], row[2], row[3], row[4], row[5], row[6], row[7],
        );

        let name = |index: i64| -> Option<String> {
            document.names.get(usize::try_from(index).ok()?).cloned()
        };

        match kind {
            DAMAGE => {
                if let Some(dealer) = players.get_mut(&source) {
                    dealer.damage_done += amount;
                }
                if let Some(victim) = players.get_mut(&target) {
                    victim.damage_taken += amount;
                    victim.damage_absorbed += a;

                    if was_caught(&caught, target, ability, at) {
                        victim.avoidable_damage += amount;
                    }
                }
                if let Some(ability_name) = name(ability) {
                    let entry =
                        abilities
                            .entry((source, ability_name.clone()))
                            .or_insert(AbilityStats {
                                combat_id: source,
                                ability: ability_name,
                                ..Default::default()
                            });
                    entry.damage += amount;
                    entry.hits += 1;
                }
            }
            HEAL => {
                if let Some(healer) = players.get_mut(&source) {
                    healer.healing_done += amount;
                    healer.overhealing += a;
                }
                if let Some(ability_name) = name(ability) {
                    let entry =
                        abilities
                            .entry((source, ability_name.clone()))
                            .or_insert(AbilityStats {
                                combat_id: source,
                                ability: ability_name,
                                ..Default::default()
                            });
                    entry.healing += amount;
                    entry.hits += 1;
                }
            }
            CAST_START => {
                if let Some(ability_name) = name(ability) {
                    let entry =
                        abilities
                            .entry((source, ability_name.clone()))
                            .or_insert(AbilityStats {
                                combat_id: source,
                                ability: ability_name,
                                ..Default::default()
                            });
                    entry.casts += 1;
                }
            }
            INTERRUPT => {
                if let Some(actor) = players.get_mut(&source) {
                    actor.interrupts += 1;
                }
            }
            DISPEL => {
                if let Some(actor) = players.get_mut(&source) {
                    actor.dispels += 1;
                }
            }
            RESOURCE_SPENT => {
                if let Some(actor) = players.get_mut(&source) {
                    actor.resource_spent += a;
                }
                if let Some(ability_name) = name(ability) {
                    let entry =
                        abilities
                            .entry((source, ability_name.clone()))
                            .or_insert(AbilityStats {
                                combat_id: source,
                                ability: ability_name,
                                ..Default::default()
                            });
                    entry.resource_spent += a;
                }
            }
            SPAWN => {
                // The SOURCE is the one who spawned. Reading it as
                // target.max(source) worked only because peer ids happen to be
                // positive: a boss at -1 or a minion at -100 was filed under 0,
                // where they overwrote one another.
                spawned.insert(source, at);
            }
            DEATH => {
                if let Some(victim) = players.get_mut(&target) {
                    victim.deaths += 1;

                    // Time on your feet, which is the honest denominator for a
                    // per-second figure: somebody who died at thirty seconds did
                    // not have the whole fight to work with.
                    let from = spawned.get(&target).copied().unwrap_or(0);
                    victim.alive_ms = (at - from).max(0);
                }
            }
            _ => {}
        }
    }

    let mut players: Vec<PlayerStats> = players.into_values().collect();
    players.sort_by(|a, b| {
        b.damage_done
            .cmp(&a.damage_done)
            .then(a.combat_id.cmp(&b.combat_id))
    });

    let mut abilities: Vec<AbilityStats> = abilities.into_values().collect();
    abilities.sort_by(|a, b| {
        a.combat_id
            .cmp(&b.combat_id)
            .then(b.damage.cmp(&a.damage))
            .then(a.ability.cmp(&b.ability))
    });

    Derived {
        format: document.format,
        events: i32::try_from(document.events.len()).unwrap_or(i32::MAX),
        truncated: document.truncated,
        players,
        abilities,
    }
}

fn was_caught(caught: &HashMap<(i64, i64), Vec<i64>>, target: i64, ability: i64, at: i64) -> bool {
    caught.get(&(target, ability)).is_some_and(|moments| {
        moments
            .iter()
            .any(|when| (when - at).abs() <= SAME_MOMENT_MS)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// alice(11) fights the boss(-1): two hits, a heal that half overheals, a
    /// telegraph she was standing inside, an interrupt, and her death.
    fn document() -> Document {
        serde_json::from_str(
            r#"{
              "format": 2,
              "duration_ms": 20000,
              "truncated": false,
              "actors": [
                {"id": 11, "name": "alice", "kind": "hero", "class": "Ember", "player_id": "alice-id"},
                {"id": -1, "name": "boss", "kind": "boss", "class": "", "player_id": ""}
              ],
              "names": ["Lance", "Mend", "Crater", "Rebuke"],
              "events": [
                [0,     10, 11, 11, -1, 0,  0, 0],
                [1000,  0,  11, -1, 0,  40, 0, 0],
                [2000,  0,  11, -1, 0,  60, 0, 0],
                [3000,  1,  11, 11, 1,  30, 70, 0],
                [4000,  4,  -1, 11, 2,  0,  -180, 1],
                [4010,  0,  -1, 11, 2,  25, 5, 0],
                [5000,  4,  -1, 11, 2,  0,  340, 0],
                [5010,  0,  -1, 11, 3,  10, 0, 0],
                [6000,  7,  11, -1, 3,  0,  0, 0],
                [6500,  11, 11, 11, 0,  0,  18, 0],
                [8000,  9,  -1, 11, 2,  0,  0, 0]
              ]
            }"#,
        )
        .unwrap()
    }

    #[test]
    fn a_wrong_format_is_refused_rather_than_half_read() {
        let bad = br#"{"format":99,"duration_ms":0,"actors":[],"names":[],"events":[]}"#;
        assert!(matches!(parse(bad), Err(LogError::UnsupportedFormat(99))));
        assert!(matches!(parse(b"not json"), Err(LogError::Unreadable(_))));

        // Specifically the previous version. Its lanes are four wide where these
        // are five, so reading one as the other would put a health bar where the
        // mana bar goes rather than failing.
        let old = br#"{"format":1,"duration_ms":0,"actors":[],"abilities":[],"events":[]}"#;
        assert!(matches!(parse(old), Err(LogError::UnsupportedFormat(1))));
    }

    #[test]
    fn what_a_rotation_cost_is_counted_too() {
        let alice = &derive(&document()).players[0];
        assert_eq!(alice.resource_spent, 18);

        let lance = derive(&document())
            .abilities
            .into_iter()
            .find(|a| a.ability == "Lance" && a.combat_id == 11)
            .expect("Lance is hers");

        assert_eq!(lance.resource_spent, 18, "and attributed to what bought it");
    }

    #[test]
    fn a_spawn_belongs_to_whoever_spawned() {
        // A NEGATIVE id, which is what a boss and every minion have. The spawn
        // used to be filed under target.max(source), so anybody at or below zero
        // landed under the same key and clobbered the others -- invisible while
        // only heroes, whose peer ids are positive, were measured.
        let mut doc = document();
        doc.actors[0].id = -7;
        for row in &mut doc.events {
            for slot in [2usize, 3usize] {
                if row[slot] == 11 {
                    row[slot] = -7;
                }
            }
        }

        let derived = derive(&doc);
        assert_eq!(derived.players.len(), 1);

        // Spawned at 0, died at 8000. Read from the wrong key it would fall back
        // to zero and give the same answer here, so the check that matters is
        // that the actor is found at all.
        assert_eq!(derived.players[0].combat_id, -7);
        assert_eq!(derived.players[0].alive_ms, 8000);
        assert_eq!(derived.players[0].damage_done, 100);
    }

    #[test]
    fn totals_come_from_the_events_and_only_heroes_get_a_row() {
        let derived = derive(&document());

        // The boss dealt damage too, and is not a contributor.
        assert_eq!(derived.players.len(), 1);

        let alice = &derived.players[0];
        assert_eq!(alice.display_name, "alice");
        assert_eq!(alice.player_id, "alice-id");
        assert_eq!(alice.damage_done, 100);
        assert_eq!(alice.damage_taken, 35);
        // Credited to whoever took the hit, not whoever landed it.
        assert_eq!(alice.damage_absorbed, 5);
        assert_eq!(alice.interrupts, 1);
        assert_eq!(alice.deaths, 1);
    }

    #[test]
    fn healing_keeps_what_was_wasted() {
        let alice = &derive(&document()).players[0];

        // Otherwise a healer topping up somebody already full looks productive.
        assert_eq!(alice.healing_done, 30);
        assert_eq!(alice.overhealing, 70);
    }

    #[test]
    fn avoidable_damage_is_the_damage_she_was_standing_in() {
        let alice = &derive(&document()).players[0];

        // Crater at 4000ms judged her 1.8m INSIDE and hit for 25. The second
        // judgement put her 3.4m outside and the following blow, from a
        // different ability, was unavoidable.
        assert_eq!(alice.damage_taken, 35);
        assert_eq!(alice.avoidable_damage, 25, "only what she stood in counts");
    }

    #[test]
    fn time_alive_is_measured_to_the_death_not_to_the_end() {
        let alice = &derive(&document()).players[0];

        // She died at eight seconds of a twenty second fight. Dividing her
        // damage by twenty would flatter everyone who survived.
        assert_eq!(alice.alive_ms, 8000);
    }

    #[test]
    fn abilities_are_broken_out_per_actor() {
        let derived = derive(&document());
        let lance = derived
            .abilities
            .iter()
            .find(|a| a.ability == "Lance" && a.combat_id == 11)
            .expect("Lance is hers");

        assert_eq!(lance.damage, 100);
        assert_eq!(lance.hits, 2);

        // The boss's abilities are attributed to the boss, not folded into hers.
        assert!(derived
            .abilities
            .iter()
            .any(|a| a.ability == "Crater" && a.combat_id == -1));
    }
}
