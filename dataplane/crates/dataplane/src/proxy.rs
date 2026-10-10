use std::sync::Arc;
use std::time::Instant;

use axum::body::{Body, Bytes};
use axum::extract::State;
use axum::http::{HeaderMap, HeaderValue, StatusCode, header};
use axum::response::{IntoResponse, Response};
use serde_json::{Value, json};
use tonic::transport::Channel;
use uuid::Uuid;

use crate::events::{AdmitGuard, EventSink, TurnIds, now_unix_ms};
use crate::observe::Format;
use crate::proto::decision::Outcome;
use crate::proto::scheduler_client::SchedulerClient;
use crate::proto::{EngineClass, Refuse, Route, ServiceClass, TurnRequest};
use crate::run_identity::{RunIndex, tool_result_ids};
use crate::turn::{ObservedBody, TurnContext, TurnTimings};

pub const RUN_ID_HEADER: &str = "x-luftpause-run-id";
pub const REPLICA_HEADER: &str = "x-luftpause-replica";
pub const CLASS_HEADER: &str = "x-luftpause-class";

#[derive(Clone)]
pub struct AppState {
    pub scheduler: SchedulerClient<Channel>,
    pub http: reqwest::Client,
    pub sink: EventSink,
    pub runs: Arc<RunIndex>,
}

pub async fn chat_completions(State(app): State<AppState>, headers: HeaderMap, body: Bytes) -> Response {
    handle(app, headers, body, EngineClass::Generative).await
}

pub async fn decide(State(app): State<AppState>, headers: HeaderMap, body: Bytes) -> Response {
    handle(app, headers, body, EngineClass::SinglePass).await
}

async fn handle(app: AppState, headers: HeaderMap, body: Bytes, engine_class: EngineClass) -> Response {
    let received = Instant::now();
    let Ok(request) = serde_json::from_slice::<Value>(&body) else {
        return error(StatusCode::BAD_REQUEST, "body is not JSON");
    };
    let tool_results = tool_result_ids(&request);
    let ids = TurnIds { turn_id: Uuid::new_v4().to_string(), run_id: resolve_run(&app, &headers, &tool_results) };
    let admit = TurnRequest {
        turn_id: ids.turn_id.clone(),
        run_id: ids.run_id.clone(),
        engine_class: engine_class as i32,
        model: request["model"].as_str().unwrap_or_default().to_owned(),
        at_unix_ms: now_unix_ms(),
        resumed: !tool_results.is_empty(),
        service_class: service_class(&headers) as i32,
    };
    let guard = AdmitGuard::new(app.sink.clone(), ids.clone());

    let decision = match app.scheduler.clone().admit(admit).await {
        Ok(response) => response.into_inner().outcome,
        Err(status) => return error(StatusCode::SERVICE_UNAVAILABLE, &format!("scheduler: {}", status.message())),
    };
    let timings = TurnTimings { received, routed_after: received.elapsed() };

    let mut response = match decision {
        Some(Outcome::Route(route)) => forward(&app, ids.clone(), route, body, timings).await,
        Some(Outcome::Refuse(refuse)) => refused(&refuse),
        None => error(StatusCode::SERVICE_UNAVAILABLE, "scheduler returned no decision"),
    };
    guard.disarm();
    if let Ok(value) = HeaderValue::from_str(&ids.run_id) {
        response.headers_mut().insert(RUN_ID_HEADER, value);
    }
    response
}

fn service_class(headers: &HeaderMap) -> ServiceClass {
    match headers.get(CLASS_HEADER).and_then(|v| v.to_str().ok()) {
        Some(v) if v.eq_ignore_ascii_case("batch") => ServiceClass::Batch,
        _ => ServiceClass::Interactive,
    }
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

async fn forward(app: &AppState, ids: TurnIds, route: Route, body: Bytes, timings: TurnTimings) -> Response {
    app.sink.started(&ids, &route.replica_id);
    let upstream = app
        .http
        .post(format!("{}{}", route.base_url.trim_end_matches('/'), engine_path(&route)))
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
    let turn = TurnContext {
        ids,
        replica_id: route.replica_id,
        timings,
        sink: app.sink.clone(),
        runs: app.runs.clone(),
    };
    let body = ObservedBody::new(Box::pin(upstream.bytes_stream()), format, turn);

    let mut builder = Response::builder().status(status).header(header::CONTENT_TYPE, content_type);
    if let Some(replica) = replica {
        builder = builder.header(REPLICA_HEADER, replica);
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
