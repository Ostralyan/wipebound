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

diesel::joinable!(run_players -> runs (run_id));
diesel::allow_tables_to_appear_in_same_query!(game_servers, runs, run_players);
