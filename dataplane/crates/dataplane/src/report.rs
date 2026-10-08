use std::time::Duration;

use tokio::sync::mpsc::{self, UnboundedReceiver};
use tokio_stream::wrappers::UnboundedReceiverStream;
use tonic::transport::Channel;

use crate::proto::TurnEvent;
use crate::proto::scheduler_client::SchedulerClient;

const RECONNECT_DELAY: Duration = Duration::from_secs(1);

pub fn spawn(client: SchedulerClient<Channel>, mut events: UnboundedReceiver<TurnEvent>) {
    tokio::spawn(async move {
        let mut pending: Option<TurnEvent> = None;
        loop {
            let (tx, rx) = mpsc::unbounded_channel();
            let mut call_client = client.clone();
            let call = tokio::spawn(async move { call_client.report(UnboundedReceiverStream::new(rx)).await });

            loop {
                let event = match pending.take() {
                    Some(event) => event,
                    None => match events.recv().await {
                        Some(event) => event,
                        None => return,
                    },
                };
                if let Err(failed) = tx.send(event) {
                    pending = Some(failed.0);
                    break;
                }
                if call.is_finished() {
                    break;
                }
            }

            if let Ok(Err(status)) = call.await {
                eprintln!("report stream ended: {status}");
            }
            tokio::time::sleep(RECONNECT_DELAY).await;
        }
    });
}
