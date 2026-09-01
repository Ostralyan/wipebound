use axum::{
    extract::{Request, State},
    middleware::Next,
    response::Response,
};

use crate::{error::AppError, AppState};

/// Gate for /v1/internal/*.
///
/// This is the line that makes a ladder mean anything: only machines holding this
/// secret can submit a run, and the secret lives on servers you operate. A player
/// with a modified client can lie to a game server all day and still has no way to
/// put a number in the database.
pub async fn require_game_server(
    State(state): State<AppState>,
    request: Request,
    next: Next,
) -> Result<Response, AppError> {
    let presented = request
        .headers()
        .get(axum::http::header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.strip_prefix("Bearer "));

    match presented {
        Some(token) if constant_time_eq(token, &state.config.game_server_token) => {
            Ok(next.run(request).await)
        }
        _ => Err(AppError::Unauthorised),
    }
}

/// Compares in time proportional to length rather than to the shared prefix, so a
/// caller cannot learn the secret one character at a time.
fn constant_time_eq(left: &str, right: &str) -> bool {
    if left.len() != right.len() {
        return false;
    }

    let mut difference = 0u8;
    for (a, b) in left.bytes().zip(right.bytes()) {
        difference |= a ^ b;
    }

    difference == 0
}
