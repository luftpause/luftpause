use std::env;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use axum::http::{HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::post;
use axum::{Json, Router};
use serde_json::{Value, json};

const PROTOCOL_VERSION: &str = "2025-06-18";
static SESSIONS: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Copy)]
struct Delay {
    median_ms: f64,
    deep_median_ms: f64,
    sigma: f64,
    max_ms: f64,
    result_chars: usize,
}

#[tokio::main]
async fn main() {
    let port: u16 = read("PORT", 9100);
    let delay = Delay {
        median_ms: read("TOOL_MS_MEDIAN", 2000.0),
        deep_median_ms: read("DEEP_MS_MEDIAN", 45000.0),
        sigma: read("TOOL_SIGMA", 1.0),
        max_ms: read("TOOL_MS_MAX", 20000.0),
        result_chars: read("RESULT_CHARS", 2000),
    };
    let router = Router::new().route("/mcp", post(move |headers, body| handle(headers, body, delay)));
    let listener = tokio::net::TcpListener::bind(("0.0.0.0", port)).await.expect("bind");
    println!("fake-mcp listening on {port}, tool delay median {} ms sigma {}", delay.median_ms, delay.sigma);
    axum::serve(listener, router).await.expect("serve");
}

async fn handle(headers: HeaderMap, Json(request): Json<Value>, delay: Delay) -> Response {
    let id = request["id"].clone();
    match request["method"].as_str().unwrap_or_default() {
        "initialize" => {
            let session = format!("fake-mcp-{}", SESSIONS.fetch_add(1, Ordering::Relaxed));
            let result = json!({
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": { "tools": {} },
                "serverInfo": { "name": "fake-mcp", "version": "0.1.0" }
            });
            ([("mcp-session-id", session)], Json(reply(id, result))).into_response()
        }
        "notifications/initialized" => StatusCode::ACCEPTED.into_response(),
        "tools/list" => Json(reply(id, json!({ "tools": [lookup_tool(), deep_search_tool()] }))).into_response(),
        "tools/call" if headers.contains_key("mcp-session-id") => {
            let median = if request["params"]["name"] == "deep_search" { delay.deep_median_ms } else { delay.median_ms };
            let pause = pause_for(&request["params"]["arguments"], median, delay);
            tokio::time::sleep(pause).await;
            let text = result_text(&request["params"]["arguments"], delay.result_chars);
            Json(reply(id, json!({ "content": [{ "type": "text", "text": text }], "isError": false }))).into_response()
        }
        "tools/call" => (StatusCode::BAD_REQUEST, "missing mcp-session-id").into_response(),
        "ping" => Json(reply(id, json!({}))).into_response(),
        other => Json(json!({ "jsonrpc": "2.0", "id": id, "error": { "code": -32601, "message": format!("unknown method {other}") } })).into_response(),
    }
}

fn pause_for(arguments: &Value, median_ms: f64, delay: Delay) -> Duration {
    let seed = arguments.to_string().bytes().fold(0x9e37_79b9_7f4a_7c15u64, |h, b| (h ^ b as u64).wrapping_mul(0x1000_0000_01b3));
    let u1 = ((seed >> 11) as f64 + 1.0) / (1u64 << 53) as f64;
    let u2 = ((seed.rotate_left(29) >> 11) as f64) / (1u64 << 53) as f64;
    let z = (-2.0 * u1.ln()).sqrt() * (std::f64::consts::TAU * u2).cos();
    Duration::from_millis((median_ms * (delay.sigma * z).exp()).min(delay.max_ms) as u64)
}

fn result_text(arguments: &Value, chars: usize) -> String {
    let mut text = format!("Result for {arguments}: rivers carry sediment downstream. ");
    while text.len() < chars {
        text.push_str("Field notes describe banks, floods, silt and the slow bend of the channel over the years. ");
    }
    text.truncate(chars.max(1));
    text
}

fn lookup_tool() -> Value {
    json!({
        "name": "lookup",
        "description": "Look up one fact for a query.",
        "inputSchema": { "type": "object", "properties": { "query": { "type": "string" } } }
    })
}

fn deep_search_tool() -> Value {
    json!({
        "name": "deep_search",
        "description": "Search the archives thoroughly for one fact. Slow.",
        "inputSchema": { "type": "object", "properties": { "query": { "type": "string" } } }
    })
}

fn reply(id: Value, result: Value) -> Value {
    json!({ "jsonrpc": "2.0", "id": id, "result": result })
}

fn read<T: std::str::FromStr>(name: &str, default: T) -> T {
    env::var(name).ok().and_then(|v| v.parse().ok()).unwrap_or(default)
}
