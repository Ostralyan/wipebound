use axum::{
    http::StatusCode,
    response::{IntoResponse, Response},
    Json,
};
use serde_json::json;

#[derive(Debug, thiserror::Error)]
pub enum AppError {
    #[error("not found")]
    NotFound,
    #[error("unauthorised")]
    Unauthorised,
    #[error("{0}")]
    Invalid(String),
    #[error("{0}")]
    Conflict(String),
    #[error("internal error")]
    Internal(String),
}

impl IntoResponse for AppError {
    fn into_response(self) -> Response {
        let (status, message) = match &self {
            AppError::NotFound => (StatusCode::NOT_FOUND, self.to_string()),
            AppError::Unauthorised => (StatusCode::UNAUTHORIZED, self.to_string()),
            AppError::Invalid(_) => (StatusCode::UNPROCESSABLE_ENTITY, self.to_string()),
            AppError::Conflict(_) => (StatusCode::CONFLICT, self.to_string()),

            // Logged in full, reported as nothing. A stranger learns that it broke,
            // never how.
            AppError::Internal(detail) => {
                tracing::error!(detail = %detail, "internal error");
                (
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "internal error".to_string(),
                )
            }
        };

        (status, Json(json!({ "error": message }))).into_response()
    }
}

impl From<diesel::result::Error> for AppError {
    fn from(error: diesel::result::Error) -> Self {
        AppError::Internal(error.to_string())
    }
}

impl From<diesel_async::pooled_connection::deadpool::PoolError> for AppError {
    fn from(error: diesel_async::pooled_connection::deadpool::PoolError) -> Self {
        AppError::Internal(error.to_string())
    }
}
