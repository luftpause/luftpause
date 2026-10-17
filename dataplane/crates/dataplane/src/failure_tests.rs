use std::io;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use axum::Router;
use axum::body::{Body, Bytes};
use axum::http::{HeaderMap, StatusCode, header};
use axum::routing::post;
use futures_util::{StreamExt, stream};
use serde_json::{Value, json};
use tokio::io::AsyncWriteExt;
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::mpsc::{self, UnboundedReceiver, UnboundedSender};
use tokio::time::timeout;
use tonic::transport::Endpoint;
use tonic::transport::server::TcpIncoming;
use tonic::{Request, Response, Status, Streaming};

use crate::auth;
use crate::events::EventSink;
use crate::proto::decision::Outcome;
use crate::proto::scheduler_client::SchedulerClient;
use crate::proto::scheduler_server::{Scheduler, SchedulerServer};
use crate::proto::turn_event::Kind;
use crate::proto::{Decision, Refuse, ReportAck, Route, TurnEvent, TurnRequest};
use crate::proxy::{self, AppState, RUN_ID_HEADER};
use crate::run_identity::RunIndex;

const WAIT: Duration = Duration::from_secs(5);
const TOKEN: &[u8] = b"data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n";

#[derive(Clone)]
enum Answer {
    Route(String),
    RouteWithKey(String, &'static str),
    Refuse(&'static str),
    Hold,
}

struct Signal(UnboundedSender<()>);

impl Drop for Signal {
    fn drop(&mut self) {
        let _ = self.0.send(());
    }
}

struct Stub {
    answer: Answer,
    admitted: UnboundedSender<()>,
    released: UnboundedSender<()>,
}

#[tonic::async_trait]
impl Scheduler for Stub {
    async fn admit(&self, _: Request<TurnRequest>) -> Result<Response<Decision>, Status> {
        self.admitted.send(()).expect("admitted");
        let outcome = match &self.answer {
            Answer::Route(base_url) => Outcome::Route(Route {
                replica_id: "r1".to_owned(),
                base_url: base_url.clone(),
                ..Default::default()
            }),
            Answer::RouteWithKey(base_url, authorization) => Outcome::Route(Route {
                replica_id: "r1".to_owned(),
                base_url: base_url.clone(),
                authorization: (*authorization).to_owned(),
                ..Default::default()
            }),
            Answer::Refuse(reason) => Outcome::Refuse(Refuse { reason: (*reason).to_owned() }),
            Answer::Hold => {
                let _released = Signal(self.released.clone());
                return std::future::pending().await;
            }
        };
        Ok(Response::new(Decision { outcome: Some(outcome) }))
    }

    async fn report(&self, _: Request<Streaming<TurnEvent>>) -> Result<Response<ReportAck>, Status> {
        Err(Status::unimplemented("report"))
    }
}

struct SchedulerStub {
    url: String,
    admitted: UnboundedReceiver<()>,
    released: UnboundedReceiver<()>,
}

async fn scheduler(answer: Answer) -> SchedulerStub {
    let (admitted_tx, admitted) = mpsc::unbounded_channel();
    let (released_tx, released) = mpsc::unbounded_channel();
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind scheduler");
    let url = format!("http://{}", listener.local_addr().expect("scheduler addr"));
    let stub = Stub { answer, admitted: admitted_tx, released: released_tx };
    tokio::spawn(
        tonic::transport::Server::builder()
            .add_service(SchedulerServer::new(stub))
            .serve_with_incoming(TcpIncoming::from(listener)),
    );
    SchedulerStub { url, admitted, released }
}

async fn serve(router: Router) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let addr = listener.local_addr().expect("addr");
    tokio::spawn(async move { axum::serve(listener, router).await.expect("serve") });
    format!("http://{addr}")
}

async fn closed_port() -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    format!("http://{}", listener.local_addr().expect("addr"))
}

struct Dataplane {
    url: String,
    events: UnboundedReceiver<TurnEvent>,
}

async fn dataplane(scheduler_url: &str) -> Dataplane {
    protected_dataplane(scheduler_url, None).await
}

async fn protected_dataplane(scheduler_url: &str, token: Option<&str>) -> Dataplane {
    let channel = Endpoint::from_shared(scheduler_url.to_owned()).expect("scheduler url").connect_lazy();
    let (events_tx, events) = mpsc::unbounded_channel();
    let state = AppState {
        scheduler: SchedulerClient::new(channel),
        http: reqwest::Client::new(),
        sink: EventSink::new(events_tx),
        runs: Arc::new(RunIndex::new()),
    };
    Dataplane { url: serve(auth::protect(proxy::router(state), token)).await, events }
}

impl Dataplane {
    async fn chat(&self) -> reqwest::Response {
        reqwest::Client::new()
            .post(format!("{}/v1/chat/completions", self.url))
            .header(RUN_ID_HEADER, "run-1")
            .header(header::CONTENT_TYPE, "application/json")
            .body(chat_request().to_string())
            .send()
            .await
            .expect("dataplane answers")
    }

    async fn events_through_completion(&mut self) -> Vec<String> {
        let mut kinds = Vec::new();
        loop {
            let event = timeout(WAIT, self.events.recv()).await.expect("event in time").expect("sink open");
            assert_eq!(event.run_id, "run-1");
            let done = matches!(event.kind, Some(Kind::Completed(_)));
            kinds.push(describe(&event));
            if done {
                return kinds;
            }
        }
    }
}

fn chat_request() -> Value {
    json!({"model": "m", "stream": true, "messages": [{"role": "user", "content": "hi"}]})
}

fn describe(event: &TurnEvent) -> String {
    match event.kind.as_ref().expect("kind") {
        Kind::Started(started) => format!("started {}", started.replica_id),
        Kind::FirstToken(_) => "first_token".to_owned(),
        Kind::Completed(completed) => format!("completed failed={}", completed.failed),
        Kind::RunEnded(_) => "run_ended".to_owned(),
    }
}

async fn error_message(response: reqwest::Response) -> String {
    let body: Value = serde_json::from_slice(&response.bytes().await.expect("error body")).expect("json error body");
    body["error"]["message"].as_str().expect("error message").to_owned()
}

fn run_id(response: &reqwest::Response) -> &str {
    response.headers()[RUN_ID_HEADER].to_str().expect("run id header")
}

#[tokio::test]
async fn a_client_leaving_while_held_cancels_admit_and_never_reaches_the_engine() {
    let engine_hits = Arc::new(AtomicUsize::new(0));
    let hits = engine_hits.clone();
    serve(Router::new().route(
        "/v1/chat/completions",
        post(move || {
            hits.fetch_add(1, Ordering::SeqCst);
            async { "{}" }
        }),
    ))
    .await;
    let mut scheduler = scheduler(Answer::Hold).await;
    let mut dataplane = dataplane(&scheduler.url).await;

    let body = chat_request().to_string();
    let mut client = TcpStream::connect(dataplane.url.trim_start_matches("http://")).await.expect("connect");
    let head = format!(
        "POST /v1/chat/completions HTTP/1.1\r\nhost: dataplane\r\ncontent-type: application/json\r\n{RUN_ID_HEADER}: run-1\r\ncontent-length: {}\r\n\r\n",
        body.len()
    );
    client.write_all(head.as_bytes()).await.expect("write head");
    client.write_all(body.as_bytes()).await.expect("write body");
    timeout(WAIT, scheduler.admitted.recv()).await.expect("admit called");
    drop(client);

    timeout(WAIT, scheduler.released.recv()).await.expect("admit call cancelled");
    assert_eq!(dataplane.events_through_completion().await, ["completed failed=true"]);
    assert_eq!(engine_hits.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn a_client_leaving_mid_stream_closes_the_engine_request_and_reports_a_failed_turn() {
    let (gone_tx, mut engine_gone) = mpsc::unbounded_channel();
    let engine = serve(Router::new().route(
        "/v1/chat/completions",
        post(move || {
            let gone = Signal(gone_tx.clone());
            async move {
                let chunks = stream::once(async { Ok::<_, io::Error>(Bytes::from_static(TOKEN)) })
                    .chain(stream::pending())
                    .map(move |chunk| {
                        let _ = &gone;
                        chunk
                    });
                ([(header::CONTENT_TYPE, "text/event-stream")], Body::from_stream(chunks))
            }
        }),
    ))
    .await;
    let scheduler = scheduler(Answer::Route(engine)).await;
    let mut dataplane = dataplane(&scheduler.url).await;

    let mut response = dataplane.chat().await;
    assert_eq!(response.status(), StatusCode::OK);
    assert_eq!(response.chunk().await.expect("first chunk").as_deref(), Some(TOKEN));
    drop(response);

    timeout(WAIT, engine_gone.recv()).await.expect("engine request closed");
    assert_eq!(
        dataplane.events_through_completion().await,
        ["started r1", "first_token", "completed failed=true"]
    );
}

#[tokio::test]
async fn an_unreachable_engine_is_a_502_and_a_failed_turn() {
    let scheduler = scheduler(Answer::Route(closed_port().await)).await;
    let mut dataplane = dataplane(&scheduler.url).await;

    let response = dataplane.chat().await;

    assert_eq!(response.status(), StatusCode::BAD_GATEWAY);
    assert_eq!(run_id(&response), "run-1");
    assert!(error_message(response).await.starts_with("engine r1: "));
    assert_eq!(dataplane.events_through_completion().await, ["started r1", "completed failed=true"]);
}

#[tokio::test]
async fn an_engine_dropping_mid_stream_cuts_the_client_stream_and_reports_a_failed_turn() {
    let engine = serve(Router::new().route(
        "/v1/chat/completions",
        post(|| async {
            let chunks = stream::once(async { Ok(Bytes::from_static(TOKEN)) }).chain(stream::once(async {
                tokio::time::sleep(Duration::from_millis(50)).await;
                Err(io::Error::other("engine died"))
            }));
            ([(header::CONTENT_TYPE, "text/event-stream")], Body::from_stream(chunks))
        }),
    ))
    .await;
    let scheduler = scheduler(Answer::Route(engine)).await;
    let mut dataplane = dataplane(&scheduler.url).await;

    let response = dataplane.chat().await;
    assert_eq!(response.status(), StatusCode::OK);
    let chunks: Vec<_> = response.bytes_stream().collect().await;

    assert!(chunks.last().expect("some chunk").is_err());
    assert_eq!(
        dataplane.events_through_completion().await,
        ["started r1", "first_token", "completed failed=true"]
    );
}

#[tokio::test]
async fn an_engine_refusing_at_once_passes_its_status_and_body_through_and_reports_a_failed_turn() {
    for status in [StatusCode::TOO_MANY_REQUESTS, StatusCode::INTERNAL_SERVER_ERROR] {
        let engine = serve(Router::new().route(
            "/v1/chat/completions",
            post(move || async move {
                (status, [(header::RETRY_AFTER, "7")], axum::Json(json!({"error": {"message": "engine says no"}})))
            }),
        ))
        .await;
        let scheduler = scheduler(Answer::Route(engine)).await;
        let mut dataplane = dataplane(&scheduler.url).await;

        let response = dataplane.chat().await;

        assert_eq!(response.status(), status);
        assert_eq!(run_id(&response), "run-1");
        assert_eq!(response.headers()[header::RETRY_AFTER], "7");
        assert_eq!(error_message(response).await, "engine says no");
        assert_eq!(dataplane.events_through_completion().await, ["started r1", "completed failed=true"]);
    }
}

#[tokio::test]
async fn an_unknown_model_is_a_404_with_no_turn_events() {
    let scheduler = scheduler(Answer::Refuse("no Generative replica serves model 'm'")).await;
    let mut dataplane = dataplane(&scheduler.url).await;

    let response = dataplane.chat().await;

    assert_eq!(response.status(), StatusCode::NOT_FOUND);
    assert_eq!(run_id(&response), "run-1");
    assert_eq!(error_message(response).await, "no Generative replica serves model 'm'");
    assert!(dataplane.events.try_recv().is_err());
}

#[tokio::test]
async fn an_unreachable_scheduler_is_a_503() {
    let mut dataplane = dataplane(&closed_port().await).await;

    let response = dataplane.chat().await;

    assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
    assert!(error_message(response).await.starts_with("scheduler: "));
    assert_eq!(dataplane.events_through_completion().await, ["completed failed=true"]);
}

#[tokio::test]
async fn the_clients_authorization_never_reaches_the_engine_only_the_routes_key_does() {
    let (seen_tx, mut seen) = mpsc::unbounded_channel();
    let engine = serve(Router::new().route(
        "/v1/chat/completions",
        post(move |headers: HeaderMap| {
            let keys: Vec<_> = headers.get_all(header::AUTHORIZATION).iter().map(|v| v.to_str().expect("ascii").to_owned()).collect();
            let _ = seen_tx.send(keys);
            async { "{}" }
        }),
    ))
    .await;
    let scheduler = scheduler(Answer::RouteWithKey(engine, "Bearer engine-key")).await;
    let dataplane = protected_dataplane(&scheduler.url, Some("client-token")).await;

    let response = reqwest::Client::new()
        .post(format!("{}/v1/chat/completions", dataplane.url))
        .header(header::AUTHORIZATION, "Bearer client-token")
        .header(header::CONTENT_TYPE, "application/json")
        .body(chat_request().to_string())
        .send()
        .await
        .expect("dataplane answers");

    assert_eq!(response.status(), StatusCode::OK);
    assert_eq!(timeout(WAIT, seen.recv()).await.expect("engine called").expect("headers"), ["Bearer engine-key"]);
}
