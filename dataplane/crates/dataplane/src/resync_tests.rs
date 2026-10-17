use std::io;
use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::Router;
use axum::body::{Body, Bytes};
use axum::http::{StatusCode, header};
use axum::routing::post;
use futures_util::{StreamExt, stream};
use serde_json::{Value, json};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::mpsc::{self, UnboundedReceiver, UnboundedSender};
use tokio::task::JoinHandle;
use tokio::time::timeout;
use tonic::transport::Endpoint;
use tonic::transport::server::TcpIncoming;
use tonic::{Request, Response, Status, Streaming};

use crate::events::EventSink;
use crate::ledger::RunLedger;
use crate::proto::decision::Outcome;
use crate::proto::scheduler_client::SchedulerClient;
use crate::proto::scheduler_server::{Scheduler, SchedulerServer};
use crate::proto::{Decision, InFlightTurn, ReportAck, ResyncAck, Route, RunPhase, ServiceClass, Snapshot, TurnEvent, TurnRequest};
use crate::proxy::{self, AppState, CALLER_HEADER, CLASS_HEADER, RUN_ID_HEADER};
use crate::report;
use crate::run_identity::RunIndex;

const WAIT: Duration = Duration::from_secs(10);
const TOKEN: &str = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n";
const TOOL_CALL: &str = concat!(
    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\"}]}}]}\n\n",
    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n",
    "data: [DONE]\n\n"
);
const STOP: &str = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

#[derive(Debug)]
enum Seen {
    Resync(Snapshot),
    Admit(TurnRequest),
}

#[derive(Clone)]
enum Admits {
    Hold,
    Route(String),
}

struct Life {
    admits: Admits,
    seen: UnboundedSender<Seen>,
}

#[tonic::async_trait]
impl Scheduler for Life {
    async fn admit(&self, request: Request<TurnRequest>) -> Result<Response<Decision>, Status> {
        self.seen.send(Seen::Admit(request.into_inner())).expect("seen");
        match &self.admits {
            Admits::Hold => std::future::pending().await,
            Admits::Route(base_url) => Ok(Response::new(Decision {
                outcome: Some(Outcome::Route(Route { replica_id: "r1".to_owned(), base_url: base_url.clone(), ..Default::default() })),
            })),
        }
    }

    async fn report(&self, request: Request<Streaming<TurnEvent>>) -> Result<Response<ReportAck>, Status> {
        let mut events = request.into_inner();
        while events.message().await?.is_some() {}
        Ok(Response::new(ReportAck {}))
    }

    async fn resync(&self, request: Request<Snapshot>) -> Result<Response<ResyncAck>, Status> {
        self.seen.send(Seen::Resync(request.into_inner())).expect("seen");
        Ok(Response::new(ResyncAck {}))
    }
}

async fn life(admits: Admits) -> (SocketAddr, UnboundedReceiver<Seen>) {
    let (seen, seen_rx) = mpsc::unbounded_channel();
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind scheduler");
    let addr = listener.local_addr().expect("scheduler addr");
    tokio::spawn(
        tonic::transport::Server::builder()
            .add_service(SchedulerServer::new(Life { admits, seen }))
            .serve_with_incoming(TcpIncoming::from(listener)),
    );
    (addr, seen_rx)
}

struct Switchboard {
    url: String,
    backend: Arc<Mutex<Option<SocketAddr>>>,
    links: Arc<Mutex<Vec<JoinHandle<()>>>>,
}

impl Switchboard {
    async fn new() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind switchboard");
        let url = format!("http://{}", listener.local_addr().expect("switchboard addr"));
        let backend: Arc<Mutex<Option<SocketAddr>>> = Arc::new(Mutex::new(None));
        let links: Arc<Mutex<Vec<JoinHandle<()>>>> = Arc::new(Mutex::new(Vec::new()));
        let (accept_backend, accept_links) = (backend.clone(), links.clone());
        tokio::spawn(async move {
            loop {
                let (mut client, _) = listener.accept().await.expect("accept");
                let Some(target) = *accept_backend.lock().expect("backend") else {
                    continue;
                };
                let link = tokio::spawn(async move {
                    if let Ok(mut upstream) = TcpStream::connect(target).await {
                        let _ = tokio::io::copy_bidirectional(&mut client, &mut upstream).await;
                    }
                });
                accept_links.lock().expect("links").push(link);
            }
        });
        Self { url, backend, links }
    }

    fn connect(&self, target: SocketAddr) {
        *self.backend.lock().expect("backend") = Some(target);
    }

    fn cut(&self) {
        *self.backend.lock().expect("backend") = None;
        for link in self.links.lock().expect("links").drain(..) {
            link.abort();
        }
    }
}

async fn engine() -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind engine");
    let addr = listener.local_addr().expect("engine addr");
    let router = Router::new().route(
        "/v1/chat/completions",
        post(|body: Bytes| async move {
            let request: Value = serde_json::from_slice(&body).expect("json");
            let chunks: stream::BoxStream<'static, Result<Bytes, io::Error>> = match request["messages"][0]["content"].as_str() {
                Some("pause") => stream::iter([Ok(Bytes::from_static(TOOL_CALL.as_bytes()))]).boxed(),
                Some("hang") => stream::iter([Ok(Bytes::from_static(TOKEN.as_bytes()))]).chain(stream::pending()).boxed(),
                _ => stream::iter([Ok(Bytes::from_static(TOKEN.as_bytes())), Ok(Bytes::from_static(STOP.as_bytes()))]).boxed(),
            };
            ([(header::CONTENT_TYPE, "text/event-stream")], Body::from_stream(chunks))
        }),
    );
    tokio::spawn(async move { axum::serve(listener, router).await.expect("serve engine") });
    format!("http://{addr}")
}

async fn dataplane(scheduler_url: &str) -> String {
    let channel = Endpoint::from_shared(scheduler_url.to_owned()).expect("scheduler url").connect_lazy();
    let scheduler = SchedulerClient::new(channel);
    let (events_tx, events_rx) = mpsc::unbounded_channel();
    let ledger = Arc::new(RunLedger::default());
    let scheduler_synced = report::spawn(scheduler.clone(), events_rx, ledger.clone());
    let state = AppState {
        scheduler,
        scheduler_synced,
        http: reqwest::Client::new(),
        sink: EventSink::new(events_tx, ledger),
        runs: Arc::new(RunIndex::new()),
    };
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind dataplane");
    let addr = listener.local_addr().expect("dataplane addr");
    tokio::spawn(async move { axum::serve(listener, proxy::router(state)).await.expect("serve dataplane") });
    format!("http://{addr}")
}

async fn chat(dataplane: String, run_id: &'static str, say: &'static str, class: &'static str, caller: &'static str) -> reqwest::Response {
    reqwest::Client::new()
        .post(format!("{dataplane}/v1/chat/completions"))
        .header(RUN_ID_HEADER, run_id)
        .header(CLASS_HEADER, class)
        .header(CALLER_HEADER, caller)
        .header(header::CONTENT_TYPE, "application/json")
        .body(json!({"model": "m", "stream": true, "messages": [{"role": "user", "content": say}]}).to_string())
        .send()
        .await
        .expect("dataplane answers")
}

async fn next(seen: &mut UnboundedReceiver<Seen>) -> Seen {
    timeout(WAIT, seen.recv()).await.expect("scheduler called in time").expect("scheduler alive")
}

async fn next_resync(seen: &mut UnboundedReceiver<Seen>) -> Snapshot {
    match next(seen).await {
        Seen::Resync(snapshot) => snapshot,
        other => panic!("expected a resync, got {other:?}"),
    }
}

async fn next_admit(seen: &mut UnboundedReceiver<Seen>) -> TurnRequest {
    match next(seen).await {
        Seen::Admit(request) => request,
        other => panic!("expected an admit, got {other:?}"),
    }
}

#[tokio::test]
async fn a_scheduler_coming_back_is_resynced_with_the_runs_seen_and_the_turns_in_flight() {
    let engine = engine().await;
    let board = Switchboard::new().await;
    let (first, mut first_seen) = life(Admits::Route(engine.clone())).await;
    board.connect(first);
    let dataplane = dataplane(&board.url).await;

    let at_startup = next_resync(&mut first_seen).await;
    assert!(at_startup.runs.is_empty() && at_startup.turns.is_empty());

    let paused = chat(dataplane.clone(), "run-a", "pause", "interactive", "alice").await;
    assert_eq!(paused.status(), StatusCode::OK);
    paused.bytes().await.expect("paused turn body");
    next_admit(&mut first_seen).await;
    let mut hanging = chat(dataplane.clone(), "run-b", "hang", "batch", "bob").await;
    assert_eq!(hanging.chunk().await.expect("first token").as_deref(), Some(TOKEN.as_bytes()));
    let hanging_turn = next_admit(&mut first_seen).await.turn_id;

    board.cut();
    let (second, mut second_seen) = life(Admits::Route(engine)).await;
    board.connect(second);
    let mut snapshot = next_resync(&mut second_seen).await;

    snapshot.runs.sort_by(|a, b| a.run_id.cmp(&b.run_id));
    let runs: Vec<_> = snapshot
        .runs
        .iter()
        .map(|r| (r.run_id.as_str(), r.phase, r.generating_turn_id.as_str(), r.last_replica_id.as_str(), r.service_class, r.caller.as_str()))
        .collect();
    assert_eq!(
        runs,
        [
            ("run-a", RunPhase::AwaitingTool as i32, "", "r1", ServiceClass::Interactive as i32, "alice"),
            ("run-b", RunPhase::Generating as i32, hanging_turn.as_str(), "r1", ServiceClass::Batch as i32, "bob"),
        ]
    );
    let turns: Vec<_> = snapshot.turns.iter().map(|t| InFlightTurn { started_unix_ms: 0, ..t.clone() }).collect();
    assert_eq!(
        turns,
        [InFlightTurn {
            turn_id: hanging_turn,
            run_id: "run-b".into(),
            replica_id: "r1".into(),
            service_class: ServiceClass::Batch as i32,
            caller: "bob".into(),
            started_unix_ms: 0,
        }]
    );
}

#[tokio::test]
async fn a_held_turn_is_admitted_again_by_the_scheduler_that_comes_back_and_completes() {
    let engine = engine().await;
    let board = Switchboard::new().await;
    let (first, mut first_seen) = life(Admits::Hold).await;
    board.connect(first);
    let dataplane = dataplane(&board.url).await;
    next_resync(&mut first_seen).await;

    let client = tokio::spawn(async move {
        let response = chat(dataplane, "run-h", "stop", "interactive", "carol").await;
        (response.status(), response.bytes().await.expect("body"))
    });
    let held = next_admit(&mut first_seen).await;

    board.cut();
    let (second, mut second_seen) = life(Admits::Route(engine)).await;
    board.connect(second);

    next_resync(&mut second_seen).await;
    assert_eq!(next_admit(&mut second_seen).await, held);
    let (status, body) = timeout(WAIT, client).await.expect("client answered in time").expect("client task");
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body, format!("{TOKEN}{STOP}").as_bytes());
}
