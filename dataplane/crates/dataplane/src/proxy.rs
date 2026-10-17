use std::sync::Arc;
use std::time::Instant;

use axum::Router;
use axum::body::{Body, Bytes};
use axum::extract::{Path, State};
use axum::http::{HeaderMap, HeaderValue, StatusCode, header};
use axum::response::{IntoResponse, Response};
use axum::routing::{delete, post};
use serde_json::{Value, json};
use tonic::metadata::MetadataMap;
use tonic::transport::Channel;
use tracing::field::Empty;
use tracing_opentelemetry::OpenTelemetrySpanExt;
use uuid::Uuid;

use crate::events::{AdmitGuard, EventSink, TurnIds, now_unix_ms};
use crate::observe::Format;
use crate::proto::decision::Outcome;
use crate::proto::scheduler_client::SchedulerClient;
use crate::proto::{EngineClass, Refuse, Route, ServiceClass, TurnRequest};
use crate::run_identity::{RunIndex, tool_result_ids};
use crate::telemetry;
use crate::turn::{ObservedBody, TurnContext, TurnTimings};

pub const RUN_ID_HEADER: &str = "x-luftpause-run-id";
pub const REPLICA_HEADER: &str = "x-luftpause-replica";
pub const CLASS_HEADER: &str = "x-luftpause-class";
pub const CALLER_HEADER: &str = "x-luftpause-caller";

const CALLER_MAX_BYTES: usize = 128;

#[derive(Clone)]
pub struct AppState {
    pub scheduler: SchedulerClient<Channel>,
    pub http: reqwest::Client,
    pub sink: EventSink,
    pub runs: Arc<RunIndex>,
}

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/v1/chat/completions", post(chat_completions))
        .route("/v1/decide", post(decide))
        .route("/v1/runs/{run_id}", delete(end_run))
        .with_state(state)
}

pub async fn chat_completions(State(app): State<AppState>, headers: HeaderMap, body: Bytes) -> Response {
    handle(app, headers, body, EngineClass::Generative).await
}

pub async fn decide(State(app): State<AppState>, headers: HeaderMap, body: Bytes) -> Response {
    handle(app, headers, body, EngineClass::SinglePass).await
}

pub async fn end_run(State(app): State<AppState>, Path(run_id): Path<String>) -> StatusCode {
    app.sink.run_ended(&run_id);
    StatusCode::NO_CONTENT
}

async fn handle(app: AppState, headers: HeaderMap, body: Bytes, engine_class: EngineClass) -> Response {
    let received = Instant::now();
    let Ok(request) = serde_json::from_slice::<Value>(&body) else {
        return error(StatusCode::BAD_REQUEST, "body is not JSON");
    };
    let tool_results = tool_result_ids(&request);
    let model = request["model"].as_str().unwrap_or_default().to_owned();
    let turn_id = Uuid::new_v4().to_string();
    let run_id = resolve_run(&app, &headers, &tool_results);
    let span = turn_span(engine_class, &model, &turn_id, &run_id);
    let _ = span.set_parent(telemetry::parent_context(&headers));
    let trace = span.context();
    let (trace_id, span_id) = telemetry::trace_ids(&trace);
    let ids = TurnIds { turn_id, run_id, trace_id, span_id };
    let admit = TurnRequest {
        turn_id: ids.turn_id.clone(),
        run_id: ids.run_id.clone(),
        engine_class: engine_class as i32,
        model,
        at_unix_ms: now_unix_ms(),
        resumed: !tool_results.is_empty(),
        service_class: service_class(&headers) as i32,
        request_bytes: body.len() as u32,
        caller: caller(&headers),
    };
    let guard = AdmitGuard::new(app.sink.clone(), ids.clone());

    let mut admit = tonic::Request::new(admit);
    *admit.metadata_mut() = MetadataMap::from_headers(telemetry::trace_headers(&trace));
    let decision = match app.scheduler.clone().admit(admit).await {
        Ok(response) => response.into_inner().outcome,
        Err(status) => return error(StatusCode::SERVICE_UNAVAILABLE, &format!("scheduler: {}", status.message())),
    };
    let timings = TurnTimings { received, routed_after: received.elapsed() };

    let mut response = match decision {
        Some(Outcome::Route(route)) => forward(&app, ids.clone(), route, body, timings, span.clone()).await,
        Some(Outcome::Refuse(refuse)) => refused(&refuse),
        None => error(StatusCode::SERVICE_UNAVAILABLE, "scheduler returned no decision"),
    };
    guard.disarm();
    span.record("http.response.status_code", response.status().as_u16());
    if !response.status().is_success() {
        span.record("otel.status_code", "error");
    }
    if let Ok(value) = HeaderValue::from_str(&ids.run_id) {
        response.headers_mut().insert(RUN_ID_HEADER, value);
    }
    response
}

fn turn_span(engine_class: EngineClass, model: &str, turn_id: &str, run_id: &str) -> tracing::Span {
    let operation = match engine_class {
        EngineClass::SinglePass => "decide",
        _ => "chat",
    };
    tracing::info_span!(
        "turn",
        otel.name = format!("{operation} {model}"),
        otel.kind = "server",
        otel.status_code = Empty,
        gen_ai.operation.name = operation,
        gen_ai.request.model = model,
        gen_ai.usage.input_tokens = Empty,
        gen_ai.usage.output_tokens = Empty,
        gen_ai.response.finish_reasons = Empty,
        http.response.status_code = Empty,
        luftpause.turn_id = turn_id,
        luftpause.run_id = run_id,
        luftpause.replica = Empty,
    )
}

fn service_class(headers: &HeaderMap) -> ServiceClass {
    match headers.get(CLASS_HEADER).and_then(|v| v.to_str().ok()) {
        Some(v) if v.eq_ignore_ascii_case("batch") => ServiceClass::Batch,
        _ => ServiceClass::Interactive,
    }
}

fn caller(headers: &HeaderMap) -> String {
    headers
        .get(CALLER_HEADER)
        .map(|v| {
            let bytes = v.as_bytes();
            String::from_utf8_lossy(&bytes[..bytes.len().min(CALLER_MAX_BYTES)]).into_owned()
        })
        .unwrap_or_default()
}

fn resolve_run(app: &AppState, headers: &HeaderMap, tool_results: &[String]) -> String {
    headers
        .get(RUN_ID_HEADER)
        .and_then(|v| v.to_str().ok())
        .filter(|v| !v.is_empty())
        .map(str::to_owned)
        .or_else(|| app.runs.resolve(tool_results))
        .unwrap_or_else(|| Uuid::new_v4().to_string())
}

async fn forward(app: &AppState, ids: TurnIds, route: Route, body: Bytes, timings: TurnTimings, span: tracing::Span) -> Response {
    app.sink.started(&ids, &route.replica_id);
    span.record("luftpause.replica", route.replica_id.as_str());
    let upstream = app
        .http
        .post(format!("{}{}", route.base_url.trim_end_matches('/'), engine_path(&route)))
        .headers(telemetry::trace_headers(&span.context()))
        .header(header::CONTENT_TYPE, "application/json")
        .body(body);
    let upstream = if route.authorization.is_empty() {
        upstream
    } else {
        upstream.header(header::AUTHORIZATION, &route.authorization)
    };
    let upstream = upstream.send().await;

    let upstream = match upstream {
        Ok(upstream) => upstream,
        Err(err) => {
            app.sink.completed(&ids, &Default::default(), true);
            return error(StatusCode::BAD_GATEWAY, &format!("engine {}: {err}", route.replica_id));
        }
    };

    let status = upstream.status();
    let content_type = upstream
        .headers()
        .get(header::CONTENT_TYPE)
        .cloned()
        .unwrap_or_else(|| HeaderValue::from_static("application/json"));
    let format = Format::from_content_type(content_type.to_str().unwrap_or_default());
    let replica = HeaderValue::from_str(&route.replica_id).ok();
    let retry_after = upstream.headers().get(header::RETRY_AFTER).cloned();
    let turn = TurnContext {
        ids,
        replica_id: route.replica_id,
        timings,
        sink: app.sink.clone(),
        runs: app.runs.clone(),
        span,
    };
    let body = ObservedBody::new(Box::pin(upstream.bytes_stream()), status.is_success(), format, turn);

    let mut builder = Response::builder().status(status).header(header::CONTENT_TYPE, content_type);
    if let Some(replica) = replica {
        builder = builder.header(REPLICA_HEADER, replica);
    }
    if let Some(retry_after) = retry_after {
        builder = builder.header(header::RETRY_AFTER, retry_after);
    }
    builder
        .body(Body::from_stream(body))
        .unwrap_or_else(|_| error(StatusCode::INTERNAL_SERVER_ERROR, "response build failed"))
}

fn engine_path(route: &Route) -> &str {
    if route.path.is_empty() { "/v1/chat/completions" } else { &route.path }
}

fn refused(refuse: &Refuse) -> Response {
    error(StatusCode::NOT_FOUND, &refuse.reason)
}

fn error(status: StatusCode, message: &str) -> Response {
    (status, axum::Json(json!({ "error": { "message": message } }))).into_response()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn with_caller(value: &[u8]) -> HeaderMap {
        let mut headers = HeaderMap::new();
        headers.insert(CALLER_HEADER, HeaderValue::from_bytes(value).unwrap());
        headers
    }

    #[test]
    fn caller_is_empty_when_absent() {
        assert_eq!(caller(&HeaderMap::new()), "");
    }

    #[test]
    fn caller_is_taken_from_the_header() {
        assert_eq!(caller(&with_caller(b"session-7")), "session-7");
    }

    #[test]
    fn caller_is_cut_at_128_bytes() {
        assert_eq!(caller(&with_caller(&[b'a'; 200])), "a".repeat(128));
    }

    #[test]
    fn caller_cut_inside_a_multibyte_character_is_kept_lossily() {
        let mut value = vec![b'a'; 127];
        value.extend_from_slice("é".as_bytes());
        assert_eq!(caller(&with_caller(&value)), format!("{}\u{FFFD}", "a".repeat(127)));
    }
}
