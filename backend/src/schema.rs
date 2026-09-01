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
    }
}

diesel::joinable!(run_players -> runs (run_id));
diesel::allow_tables_to_appear_in_same_query!(game_servers, runs, run_players);
