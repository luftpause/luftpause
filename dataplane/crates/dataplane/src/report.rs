use std::sync::Arc;
use std::time::Duration;

use tokio::sync::mpsc::{self, UnboundedReceiver};
use tokio::sync::watch;
use tokio_stream::wrappers::UnboundedReceiverStream;
use tonic::transport::Channel;

use crate::events::now_unix_ms;
use crate::ledger::RunLedger;
use crate::proto::TurnEvent;
use crate::proto::scheduler_client::SchedulerClient;

const RECONNECT_DELAY: Duration = Duration::from_secs(1);

pub fn spawn(client: SchedulerClient<Channel>, events: UnboundedReceiver<TurnEvent>, ledger: Arc<RunLedger>) -> watch::Receiver<bool> {
    let (synced, synced_rx) = watch::channel(false);
    tokio::spawn(stay_synced(client, events, ledger, synced));
    synced_rx
}

async fn stay_synced(
    client: SchedulerClient<Channel>,
    mut events: UnboundedReceiver<TurnEvent>,
    ledger: Arc<RunLedger>,
    synced: watch::Sender<bool>,
) {
    let mut pending: Option<TurnEvent> = None;
    loop {
        match client.clone().resync(ledger.snapshot(now_unix_ms())).await {
            Ok(_) => {
                synced.send_replace(true);
                let open = report(&client, &mut events, &mut pending).await;
                synced.send_replace(false);
                if !open {
                    return;
                }
            }
            Err(status) => eprintln!("resync failed: {status}"),
        }
        tokio::time::sleep(RECONNECT_DELAY).await;
    }
}

async fn report(client: &SchedulerClient<Channel>, events: &mut UnboundedReceiver<TurnEvent>, pending: &mut Option<TurnEvent>) -> bool {
    let (tx, rx) = mpsc::unbounded_channel();
    let mut call_client = client.clone();
    let mut call = tokio::spawn(async move { call_client.report(UnboundedReceiverStream::new(rx)).await });

    let ended = loop {
        let event = match pending.take() {
            Some(event) => event,
            None => tokio::select! {
                ended = &mut call => break ended,
                received = events.recv() => match received {
                    Some(event) => event,
                    None => return false,
                },
            },
        };
        if let Err(failed) = tx.send(event) {
            *pending = Some(failed.0);
            break (&mut call).await;
        }
    };

    if let Ok(Err(status)) = ended {
        eprintln!("report stream ended: {status}");
    }
    true
}
