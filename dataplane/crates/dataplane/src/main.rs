mod auth;
mod events;
#[cfg(test)]
mod failure_tests;
mod observe;
mod proxy;
mod report;
mod run_identity;
mod telemetry;
mod turn;

mod proto {
    tonic::include_proto!("luftpause.v1");
}

use std::env;
use std::sync::Arc;
use std::time::Duration;

use tokio::sync::mpsc;
use tonic::transport::Endpoint;

use events::EventSink;
use proto::scheduler_client::SchedulerClient;
use proxy::AppState;

#[tokio::main]
async fn main() {
    let _tracer_provider = telemetry::init();
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
    let token = env::var("LUFTPAUSE_TOKEN").ok().filter(|token| !token.is_empty());
    let router = auth::protect(proxy::router(state), token.as_deref());

    let listener = tokio::net::TcpListener::bind(&listen).await.expect("bind");
    eprintln!("dataplane listening on {listen}, scheduler {scheduler_url}");
    axum::serve(listener, router).await.expect("serve");
}
