// Hand-written to match migrations/. Regenerate with `diesel print-schema`
// once a database exists.

diesel::table! {
    game_servers (id) {
        id -> Text,
        region -> Text,
        last_seen_at -> Timestamptz,
    }
}

diesel::table! {
    runs (id) {
        id -> Text,
        boss -> Text,
        outcome -> Text,
        duration_ms -> Int8,
        content_hash -> Text,
        engine -> Text,
        authority -> Text,
        submission_digest -> Text,
        rankable -> Bool,
        unrankable_reason -> Nullable<Text>,
        worst_overreach_cm -> Int8,
        game_server -> Text,
        created_at -> Timestamptz,
    }
}

diesel::table! {
    run_players (run_id, peer) {
        run_id -> Text,
        peer -> Int8,
        damage_done -> Int8,
        healing_done -> Int8,
        damage_taken -> Int8,
        overreach_cm -> Int8,
        // Appended by 2026-09-01-000000_player_identity. Listed last because
        // that is where ALTER TABLE ADD COLUMN puts them, and Queryable maps
        // by position: a hand-written schema that disagrees with the database
        // is a bug waiting for the first query that does not use Selectable.
        player_id -> Text,
        display_name -> Text,
        identity -> Text,
    }
}

diesel::table! {
    run_logs (run_id) {
        run_id -> Text,
        format -> Int4,
        body -> Bytea,
        byte_size -> Int8,
        events -> Int4,
        truncated -> Bool,
        created_at -> Timestamptz,
        log_digest -> Text,
    }
}

diesel::table! {
    run_player_stats (run_id, combat_id) {
        run_id -> Text,
        combat_id -> Int8,
        player_id -> Text,
        display_name -> Text,
        class_name -> Text,
        damage_done -> Int8,
        healing_done -> Int8,
        overhealing -> Int8,
        damage_taken -> Int8,
        damage_absorbed -> Int8,
        avoidable_damage -> Int8,
        interrupts -> Int4,
        dispels -> Int4,
        deaths -> Int4,
        alive_ms -> Int8,
        resource_spent -> Int8,
    }
}

diesel::table! {
    run_ability_stats (run_id, combat_id, ability) {
        run_id -> Text,
        combat_id -> Int8,
        ability -> Text,
        damage -> Int8,
        healing -> Int8,
        hits -> Int4,
        casts -> Int4,
        resource_spent -> Int8,
    }
}

diesel::joinable!(run_players -> runs (run_id));
diesel::joinable!(run_logs -> runs (run_id));
diesel::joinable!(run_player_stats -> runs (run_id));
diesel::joinable!(run_ability_stats -> runs (run_id));
diesel::allow_tables_to_appear_in_same_query!(
    game_servers,
    runs,
    run_players,
    run_logs,
    run_player_stats,
    run_ability_stats
);
