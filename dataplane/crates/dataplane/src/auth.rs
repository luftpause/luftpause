use std::sync::Arc;

use axum::Router;
use axum::extract::{Request, State};
use axum::http::{StatusCode, header};
use axum::middleware::{self, Next};
use axum::response::{IntoResponse, Response};
use serde_json::json;

pub fn protect(router: Router, token: Option<&str>) -> Router {
    match token {
        Some(token) => {
            let expected: Arc<[u8]> = format!("Bearer {token}").into_bytes().into();
            router.layer(middleware::from_fn_with_state(expected, require_bearer))
        }
        None => {
            eprintln!("warning: LUFTPAUSE_TOKEN is not set, the proxy is open to anyone who can reach it");
            router
        }
    }
}

async fn require_bearer(State(expected): State<Arc<[u8]>>, request: Request, next: Next) -> Response {
    let presented = request.headers().get(header::AUTHORIZATION).map(|v| v.as_bytes());
    if presented.is_some_and(|presented| constant_time_eq(presented, &expected)) {
        next.run(request).await
    } else {
        unauthorized()
    }
}

fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

fn unauthorized() -> Response {
    (
        StatusCode::UNAUTHORIZED,
        [(header::WWW_AUTHENTICATE, "Bearer")],
        axum::Json(json!({ "error": { "message": "missing or invalid bearer token" } })),
    )
        .into_response()
}

#[cfg(test)]
mod tests {
    use axum::routing::post;
    use serde_json::Value;
    use tokio::net::TcpListener;

    use super::*;

    async fn serve(token: Option<&str>) -> String {
        let router = protect(Router::new().route("/v1/decide", post(|| async { "ok" })), token);
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let addr = listener.local_addr().expect("addr");
        tokio::spawn(async move { axum::serve(listener, router).await.expect("serve") });
        format!("http://{addr}/v1/decide")
    }

    async fn call(url: &str, authorization: Option<&str>) -> reqwest::Response {
        let request = reqwest::Client::new().post(url);
        let request = match authorization {
            Some(value) => request.header(header::AUTHORIZATION, value),
            None => request,
        };
        request.send().await.expect("answers")
    }

    #[tokio::test]
    async fn a_request_without_the_token_is_a_401_with_a_json_error() {
        let url = serve(Some("s3cret")).await;

        let response = call(&url, None).await;

        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
        let body: Value = serde_json::from_slice(&response.bytes().await.expect("body")).expect("json");
        assert_eq!(body["error"]["message"], "missing or invalid bearer token");
    }

    #[tokio::test]
    async fn a_request_with_a_wrong_token_is_a_401() {
        let url = serve(Some("s3cret")).await;

        for wrong in ["Bearer s3crex", "Bearer s3cret2", "s3cret", "Basic s3cret"] {
            assert_eq!(call(&url, Some(wrong)).await.status(), StatusCode::UNAUTHORIZED, "{wrong}");
        }
    }

    #[tokio::test]
    async fn a_request_with_the_token_is_served() {
        let url = serve(Some("s3cret")).await;

        let response = call(&url, Some("Bearer s3cret")).await;

        assert_eq!(response.status(), StatusCode::OK);
    }

    #[tokio::test]
    async fn without_a_token_everything_is_served() {
        let url = serve(None).await;

        assert_eq!(call(&url, None).await.status(), StatusCode::OK);
        assert_eq!(call(&url, Some("Bearer anything")).await.status(), StatusCode::OK);
    }

    #[test]
    fn constant_time_eq_compares_whole_slices() {
        assert!(constant_time_eq(b"abc", b"abc"));
        assert!(!constant_time_eq(b"abc", b"abd"));
        assert!(!constant_time_eq(b"abc", b"abcd"));
        assert!(!constant_time_eq(b"", b"a"));
    }
}
