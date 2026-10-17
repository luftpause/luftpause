use std::time::{SystemTime, UNIX_EPOCH};

use tokio::sync::mpsc::UnboundedSender;

use crate::observe::Outcome;
use crate::proto::{FirstToken, RunEnded, TurnCompleted, TurnEvent, TurnStarted, turn_event::Kind};

#[derive(Clone)]
pub struct EventSink(UnboundedSender<TurnEvent>);

#[derive(Clone)]
pub struct TurnIds {
    pub turn_id: String,
    pub run_id: String,
    pub trace_id: String,
    pub span_id: String,
}

impl EventSink {
    pub fn new(sender: UnboundedSender<TurnEvent>) -> Self {
        Self(sender)
    }

    pub fn started(&self, ids: &TurnIds, replica_id: &str) {
        self.emit(ids, Kind::Started(TurnStarted { replica_id: replica_id.to_owned() }));
    }

    pub fn first_token(&self, ids: &TurnIds) {
        self.emit(ids, Kind::FirstToken(FirstToken {}));
    }

    pub fn completed(&self, ids: &TurnIds, outcome: &Outcome, failed: bool) {
        self.emit(
            ids,
            Kind::Completed(TurnCompleted {
                finish_reason: outcome.finish_reason.clone().unwrap_or_default(),
                prompt_tokens: outcome.prompt_tokens,
                completion_tokens: outcome.completion_tokens,
                cached_tokens: outcome.cached_tokens,
                failed,
            }),
        );
    }

    pub fn run_ended(&self, run_id: &str) {
        let ids = TurnIds { turn_id: String::new(), run_id: run_id.to_owned(), trace_id: String::new(), span_id: String::new() };
        self.emit(&ids, Kind::RunEnded(RunEnded {}));
    }

    fn emit(&self, ids: &TurnIds, kind: Kind) {
        let _ = self.0.send(TurnEvent {
            turn_id: ids.turn_id.clone(),
            run_id: ids.run_id.clone(),
            at_unix_ms: now_unix_ms(),
            kind: Some(kind),
            trace_id: ids.trace_id.clone(),
            span_id: ids.span_id.clone(),
        });
    }
}

pub fn now_unix_ms() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_millis() as i64)
}

pub struct AdmitGuard {
    sink: EventSink,
    ids: TurnIds,
    armed: bool,
}

impl AdmitGuard {
    pub fn new(sink: EventSink, ids: TurnIds) -> Self {
        Self { sink, ids, armed: true }
    }

    pub fn disarm(mut self) {
        self.armed = false;
    }
}

impl Drop for AdmitGuard {
    fn drop(&mut self) {
        if self.armed {
            self.sink.completed(&self.ids, &Outcome::default(), true);
        }
    }
}
