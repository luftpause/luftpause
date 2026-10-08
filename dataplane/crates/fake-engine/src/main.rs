mod config;
mod openai;
mod script;

use std::convert::Infallible;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use axum::body::{Body, Bytes};
use axum::extract::State;
use axum::http::{HeaderMap, StatusCode, header};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use serde_json::{Value, json};
use tokio::sync::mpsc;
use tokio_stream::wrappers::ReceiverStream;

use config::EngineConfig;
use script::{Plan, Reply};

#[derive(Clone)]
struct App {
    config: EngineConfig,
    counter: Arc<AtomicU64>,
    instance: u64,
    api_key: Option<Arc<str>>,
}

#[tokio::main]
async fn main() {
    let config = EngineConfig::from_env();
    let app = App {
        config,
        counter: Arc::new(AtomicU64::new(0)),
        instance: SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_nanos() as u64),
        api_key: std::env::var("API_KEY").ok().filter(|k| !k.is_empty()).map(Arc::from),
    };
    let router = Router::new()
        .route("/v1/chat/completions", post(chat))
        .route("/classify", post(classify))
        .route("/v1/models", get(models))
        .route("/health", get(|| async { StatusCode::OK }))
        .with_state(app);
    let listener = tokio::net::TcpListener::bind(("0.0.0.0", config.port)).await.expect("bind");
    println!("fake-engine listening on {} with {config:?}", config.port);
    axum::serve(listener, router).await.expect("serve");
}

async fn classify(State(app): State<App>, headers: HeaderMap, Json(request): Json<Value>) -> Response {
    if !authorized(&app, &headers) {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    let inputs: Vec<String> = match &request["input"] {
        Value::String(text) => vec![text.clone()],
        Value::Array(items) => items.iter().filter_map(|v| v.as_str().map(str::to_owned)).collect(),
        _ => return (StatusCode::BAD_REQUEST, "input must be a string or a list of strings").into_response(),
    };
    let tokens: usize = inputs.iter().map(|t| t.len().div_ceil(4)).sum();
    tokio::time::sleep(app.config.prefill_per_token * tokens as u32).await;
    let id = format!("classify-{:x}-{}", app.instance, app.counter.fetch_add(1, Ordering::Relaxed));
    Json(openai::classification(&id, request["model"].as_str().unwrap_or("fake"), &inputs)).into_response()
}

async fn models(State(app): State<App>, headers: HeaderMap) -> Response {
    if !authorized(&app, &headers) {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    Json(json!({ "object": "list", "data": [{ "id": "fake", "object": "model" }] })).into_response()
}

fn authorized(app: &App, headers: &HeaderMap) -> bool {
    let Some(key) = &app.api_key else {
        return true;
    };
    headers
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
        .is_some_and(|given| given == key.as_ref())
}

async fn chat(State(app): State<App>, headers: HeaderMap, Json(request): Json<Value>) -> Response {
    if !authorized(&app, &headers) {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    let plan = script::plan(&request, &app.config);
    let id = format!("fake-{:x}-{}", app.instance, app.counter.fetch_add(1, Ordering::Relaxed));

    tokio::time::sleep(app.config.prefill_per_token * plan.prompt_tokens as u32).await;

    if plan.stream {
        stream_reply(id, plan, app.config)
    } else {
        tokio::time::sleep(app.config.decode_per_token * plan.reply.completion_tokens() as u32).await;
        Json(openai::completion(&id, &plan)).into_response()
    }
}

fn stream_reply(id: String, plan: Plan, config: EngineConfig) -> Response {
    let (tx, rx) = mpsc::channel::<Result<Bytes, Infallible>>(16);
    tokio::spawn(async move {
        let send = |text: String| {
            let tx = tx.clone();
            async move { tx.send(Ok(Bytes::from(text))).await.is_ok() }
        };
        if !send(openai::sse(&openai::chunk(&id, &plan.model, json!({"role": "assistant"}), None))).await {
            return;
        }
        match plan.reply {
            Reply::ToolCall { round } => {
                tokio::time::sleep(config.decode_per_token * plan.reply.completion_tokens() as u32).await;
                let delta = json!({ "tool_calls": [openai::tool_call(&id, round)] });
                if !send(openai::sse(&openai::chunk(&id, &plan.model, delta, None))).await {
                    return;
                }
            }
            Reply::Text { tokens } => {
                for _ in 0..tokens {
                    tokio::time::sleep(config.decode_per_token).await;
                    if !send(openai::sse(&openai::chunk(&id, &plan.model, json!({"content": "tok "}), None))).await {
                        return;
                    }
                }
            }
        }
        let finish = openai::chunk(&id, &plan.model, json!({}), Some(plan.reply.finish_reason()));
        let _ = send(openai::sse(&finish)).await
            && send(openai::sse(&openai::usage_chunk(&id, &plan))).await
            && send(openai::SSE_DONE.to_owned()).await;
    });

    Response::builder()
        .header(header::CONTENT_TYPE, "text/event-stream")
        .header(header::CACHE_CONTROL, "no-cache")
        .body(Body::from_stream(ReceiverStream::new(rx)))
        .expect("response")
}
