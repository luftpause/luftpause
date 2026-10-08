mod events;
mod observe;
mod proxy;
mod report;
mod run_identity;
mod turn;

mod proto {
    tonic::include_proto!("luftpause.v1");
}

use std::env;
use std::sync::Arc;
use std::time::Duration;

use axum::Router;
use axum::routing::post;
use tokio::sync::mpsc;
use tonic::transport::Endpoint;

use events::EventSink;
use proto::scheduler_client::SchedulerClient;
use proxy::AppState;

#[tokio::main]
async fn main() {
    let listen = env::var("LISTEN_ADDR").unwrap_or_else(|_| "0.0.0.0:8080".to_owned());
    let scheduler_url = env::var("SCHEDULER_URL").unwrap_or_else(|_| "http://localhost:5000".to_owned());

    let channel = Endpoint::from_shared(scheduler_url.clone())
        .expect("SCHEDULER_URL")
        .connect_timeout(Duration::from_secs(2))
        .connect_lazy();
    let scheduler = SchedulerClient::new(channel);
    let (events_tx, events_rx) = mpsc::unbounded_channel();
    report::spawn(scheduler.clone(), events_rx);

    let state = AppState {
        scheduler,
        http: reqwest::Client::new(),
        sink: EventSink::new(events_tx),
        runs: Arc::new(run_identity::RunIndex::new()),
    };
    let router = Router::new()
        .route("/v1/chat/completions", post(proxy::chat_completions))
        .route("/v1/decide", post(proxy::decide))
        .with_state(state);

    let listener = tokio::net::TcpListener::bind(&listen).await.expect("bind");
    eprintln!("dataplane listening on {listen}, scheduler {scheduler_url}");
    axum::serve(listener, router).await.expect("serve");
}
