use std::pin::Pin;
use std::sync::Arc;
use std::task::{Context, Poll};
use std::time::{Duration, Instant};

use bytes::Bytes;
use futures_util::Stream;
use serde_json::json;

use crate::events::{EventSink, TurnIds};
use crate::observe::{Format, Observer};
use crate::run_identity::RunIndex;

type Upstream = Pin<Box<dyn Stream<Item = Result<Bytes, reqwest::Error>> + Send>>;

pub struct TurnTimings {
    pub received: Instant,
    pub routed_after: Duration,
}

pub struct TurnContext {
    pub ids: TurnIds,
    pub replica_id: String,
    pub timings: TurnTimings,
    pub sink: EventSink,
    pub runs: Arc<RunIndex>,
}

pub struct ObservedBody {
    upstream: Upstream,
    upstream_done: bool,
    accepted: bool,
    format: Format,
    held: Vec<u8>,
    observer: Option<Observer>,
    first_token_after: Option<Duration>,
    turn: TurnContext,
}

impl ObservedBody {
    pub fn new(upstream: Upstream, accepted: bool, format: Format, turn: TurnContext) -> Self {
        Self {
            upstream,
            upstream_done: false,
            accepted,
            format,
            held: Vec::new(),
            observer: Some(Observer::new(format)),
            first_token_after: None,
            turn,
        }
    }

    fn observe(&mut self, chunk: &[u8]) {
        let Some(observer) = self.observer.as_mut() else {
            return;
        };
        let observation = observer.feed(chunk);
        self.remember_tool_calls(&observation.tool_call_ids);
        if observation.first_token {
            self.mark_first_token();
        }
    }

    fn mark_first_token(&mut self) {
        if self.first_token_after.is_none() {
            self.first_token_after = Some(self.turn.timings.received.elapsed());
            self.turn.sink.first_token(&self.turn.ids);
        }
    }

    fn remember_tool_calls(&self, tool_call_ids: &[String]) {
        for id in tool_call_ids {
            self.turn.runs.record(id, &self.turn.ids.run_id);
        }
    }

    fn finish(&mut self, broken: bool) {
        let Some(observer) = self.observer.take() else {
            return;
        };
        let failed = broken || !self.accepted;
        let outcome = observer.finish();
        self.remember_tool_calls(&outcome.tool_call_ids);
        if self.format == Format::Json && !failed {
            self.mark_first_token();
        }
        self.turn.sink.completed(&self.turn.ids, &outcome, failed);
        println!(
            "{}",
            json!({
                "turn": self.turn.ids.turn_id,
                "run": self.turn.ids.run_id,
                "replica": self.turn.replica_id,
                "queue_ms": millis(self.turn.timings.routed_after),
                "ttft_ms": self.first_token_after.map(millis),
                "total_ms": millis(self.turn.timings.received.elapsed()),
                "prompt_tokens": outcome.prompt_tokens,
                "completion_tokens": outcome.completion_tokens,
                "cached_tokens": outcome.cached_tokens,
                "finish_reason": outcome.finish_reason,
                "failed": failed,
            })
        );
    }
}

impl Stream for ObservedBody {
    type Item = Result<Bytes, reqwest::Error>;

    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        loop {
            if self.upstream_done {
                return Poll::Ready(None);
            }
            match self.upstream.as_mut().poll_next(cx) {
                Poll::Pending => return Poll::Pending,
                Poll::Ready(Some(Ok(chunk))) => {
                    self.observe(&chunk);
                    if self.format == Format::Sse {
                        return Poll::Ready(Some(Ok(chunk)));
                    }
                    self.held.extend_from_slice(&chunk);
                }
                Poll::Ready(Some(Err(err))) => {
                    self.upstream_done = true;
                    self.finish(true);
                    return Poll::Ready(Some(Err(err)));
                }
                Poll::Ready(None) => {
                    self.upstream_done = true;
                    self.finish(false);
                    let held = std::mem::take(&mut self.held);
                    return Poll::Ready((!held.is_empty()).then(|| Ok(Bytes::from(held))));
                }
            }
        }
    }
}

impl Drop for ObservedBody {
    fn drop(&mut self) {
        self.finish(true);
    }
}

fn millis(duration: Duration) -> f64 {
    (duration.as_secs_f64() * 10_000.0).round() / 10.0
}
