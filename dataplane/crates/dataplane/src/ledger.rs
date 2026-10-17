use std::collections::HashMap;
use std::sync::Mutex;

use crate::proto::{EngineClass, InFlightTurn, RunPhase, RunState, Snapshot, TurnRequest};

const RUN_IDLE_TIMEOUT_MS: i64 = 30 * 60 * 1000;
const PRUNE_EVERY_MS: i64 = 60 * 1000;

#[derive(Clone, Debug, PartialEq)]
enum Phase {
    Generating(String),
    AwaitingTool(i64),
    Idle(i64),
}

#[derive(Clone, Debug)]
struct RunEntry {
    phase: Phase,
    last_replica_id: String,
    service_class: i32,
    caller: String,
    last_activity_ms: i64,
    started_ms: i64,
    turns: u32,
}

#[derive(Default)]
pub struct Ledger {
    runs: HashMap<String, RunEntry>,
    turns: HashMap<String, InFlightTurn>,
    pruned_at_ms: i64,
}

impl Ledger {
    pub fn started(&mut self, request: &TurnRequest, replica_id: &str, at_ms: i64) {
        if request.engine_class == EngineClass::Generative as i32 {
            self.start_run_turn(request, replica_id, at_ms);
        }
        self.turns.insert(
            request.turn_id.clone(),
            InFlightTurn {
                turn_id: request.turn_id.clone(),
                run_id: request.run_id.clone(),
                replica_id: replica_id.to_owned(),
                service_class: request.service_class,
                caller: request.caller.clone(),
                started_unix_ms: at_ms,
            },
        );
        if at_ms - self.pruned_at_ms > PRUNE_EVERY_MS {
            self.prune(at_ms);
        }
    }

    fn start_run_turn(&mut self, request: &TurnRequest, replica_id: &str, at_ms: i64) {
        let run = self.runs.entry(request.run_id.clone()).or_insert_with(|| RunEntry {
            phase: Phase::Idle(at_ms),
            last_replica_id: String::new(),
            service_class: request.service_class,
            caller: request.caller.clone(),
            last_activity_ms: at_ms,
            started_ms: at_ms,
            turns: 0,
        });
        run.phase = Phase::Generating(request.turn_id.clone());
        run.last_replica_id = replica_id.to_owned();
        run.service_class = request.service_class;
        run.last_activity_ms = at_ms;
        run.turns += 1;
    }

    pub fn completed(&mut self, turn_id: &str, opens_tool_pause: bool, at_ms: i64) {
        let Some(turn) = self.turns.remove(turn_id) else {
            return;
        };
        if let Some(run) = self.runs.get_mut(&turn.run_id)
            && run.phase == Phase::Generating(turn.turn_id)
        {
            run.phase = if opens_tool_pause { Phase::AwaitingTool(at_ms) } else { Phase::Idle(at_ms) };
            run.last_activity_ms = at_ms;
        }
    }

    pub fn ended(&mut self, run_id: &str) {
        self.runs.remove(run_id);
    }

    pub fn prune(&mut self, now_ms: i64) {
        self.runs.retain(|_, run| matches!(run.phase, Phase::Generating(_)) || now_ms - run.last_activity_ms <= RUN_IDLE_TIMEOUT_MS);
        self.pruned_at_ms = now_ms;
    }

    pub fn snapshot(&self, at_ms: i64) -> Snapshot {
        Snapshot {
            runs: self.runs.iter().map(|(run_id, run)| run_state(run_id, run)).collect(),
            turns: self.turns.values().cloned().collect(),
            at_unix_ms: at_ms,
        }
    }
}

fn run_state(run_id: &str, run: &RunEntry) -> RunState {
    let (phase, generating_turn_id, phase_since_unix_ms) = match &run.phase {
        Phase::Generating(turn_id) => (RunPhase::Generating, turn_id.clone(), 0),
        Phase::AwaitingTool(since) => (RunPhase::AwaitingTool, String::new(), *since),
        Phase::Idle(since) => (RunPhase::Idle, String::new(), *since),
    };
    RunState {
        run_id: run_id.to_owned(),
        phase: phase as i32,
        generating_turn_id,
        phase_since_unix_ms,
        last_replica_id: run.last_replica_id.clone(),
        service_class: run.service_class,
        caller: run.caller.clone(),
        last_activity_unix_ms: run.last_activity_ms,
        started_unix_ms: run.started_ms,
        turns: run.turns,
    }
}

#[derive(Default)]
pub struct RunLedger(Mutex<Ledger>);

impl RunLedger {
    pub fn started(&self, request: &TurnRequest, replica_id: &str, at_ms: i64) {
        self.0.lock().expect("run ledger").started(request, replica_id, at_ms);
    }

    pub fn completed(&self, turn_id: &str, opens_tool_pause: bool, at_ms: i64) {
        self.0.lock().expect("run ledger").completed(turn_id, opens_tool_pause, at_ms);
    }

    pub fn ended(&self, run_id: &str) {
        self.0.lock().expect("run ledger").ended(run_id);
    }

    pub fn snapshot(&self, at_ms: i64) -> Snapshot {
        let mut ledger = self.0.lock().expect("run ledger");
        ledger.prune(at_ms);
        ledger.snapshot(at_ms)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::proto::ServiceClass;

    const MINUTE: i64 = 60 * 1000;

    fn request(turn_id: &str, run_id: &str, engine_class: EngineClass, service_class: ServiceClass, caller: &str) -> TurnRequest {
        TurnRequest {
            turn_id: turn_id.to_owned(),
            run_id: run_id.to_owned(),
            engine_class: engine_class as i32,
            service_class: service_class as i32,
            caller: caller.to_owned(),
            ..Default::default()
        }
    }

    fn chat(turn_id: &str, run_id: &str) -> TurnRequest {
        request(turn_id, run_id, EngineClass::Generative, ServiceClass::Interactive, "c1")
    }

    fn run<'a>(snapshot: &'a Snapshot, run_id: &str) -> &'a RunState {
        snapshot.runs.iter().find(|r| r.run_id == run_id).expect("run in snapshot")
    }

    fn turn_ids(snapshot: &Snapshot) -> Vec<&str> {
        let mut ids: Vec<_> = snapshot.turns.iter().map(|t| t.turn_id.as_str()).collect();
        ids.sort();
        ids
    }

    #[test]
    fn a_started_turn_is_in_flight_and_its_run_is_generating_on_its_replica() {
        let mut ledger = Ledger::default();
        ledger.started(&request("t1", "run-1", EngineClass::Generative, ServiceClass::Batch, "c7"), "r2", 100);

        let snapshot = ledger.snapshot(150);

        assert_eq!(
            snapshot.turns,
            [InFlightTurn {
                turn_id: "t1".into(),
                run_id: "run-1".into(),
                replica_id: "r2".into(),
                service_class: ServiceClass::Batch as i32,
                caller: "c7".into(),
                started_unix_ms: 100,
            }]
        );
        assert_eq!(
            run(&snapshot, "run-1"),
            &RunState {
                run_id: "run-1".into(),
                phase: RunPhase::Generating as i32,
                generating_turn_id: "t1".into(),
                phase_since_unix_ms: 0,
                last_replica_id: "r2".into(),
                service_class: ServiceClass::Batch as i32,
                caller: "c7".into(),
                last_activity_unix_ms: 100,
                started_unix_ms: 100,
                turns: 1,
            }
        );
        assert_eq!(snapshot.at_unix_ms, 150);
    }

    #[test]
    fn a_tool_calls_finish_leaves_the_run_awaiting_its_tool() {
        let mut ledger = Ledger::default();
        ledger.started(&chat("t1", "run-1"), "r1", 100);
        ledger.completed("t1", true, 200);

        let snapshot = ledger.snapshot(300);

        assert!(snapshot.turns.is_empty());
        let run = run(&snapshot, "run-1");
        assert_eq!((run.phase, run.phase_since_unix_ms, run.last_activity_unix_ms), (RunPhase::AwaitingTool as i32, 200, 200));
        assert_eq!(run.last_replica_id, "r1");
    }

    #[test]
    fn any_other_finish_leaves_the_run_idle() {
        let mut ledger = Ledger::default();
        ledger.started(&chat("t1", "run-1"), "r1", 100);
        ledger.completed("t1", false, 200);

        let snapshot = ledger.snapshot(300);

        let run = run(&snapshot, "run-1");
        assert_eq!((run.phase, run.phase_since_unix_ms), (RunPhase::Idle as i32, 200));
    }

    #[test]
    fn a_later_turn_keeps_the_runs_start_and_caller_and_moves_its_replica_and_class() {
        let mut ledger = Ledger::default();
        ledger.started(&chat("t1", "run-1"), "r1", 100);
        ledger.completed("t1", true, 200);
        ledger.started(&request("t2", "run-1", EngineClass::Generative, ServiceClass::Batch, "c9"), "r2", 300);

        let snapshot = ledger.snapshot(400);

        let run = run(&snapshot, "run-1");
        assert_eq!(run.generating_turn_id, "t2");
        assert_eq!(run.last_replica_id, "r2");
        assert_eq!(run.service_class, ServiceClass::Batch as i32);
        assert_eq!((run.caller.as_str(), run.started_unix_ms, run.turns), ("c1", 100, 2));
    }

    #[test]
    fn an_overlapping_turn_finishing_does_not_change_the_runs_phase() {
        let mut ledger = Ledger::default();
        ledger.started(&chat("t1", "run-1"), "r1", 100);
        ledger.started(&chat("t2", "run-1"), "r1", 150);
        ledger.completed("t1", true, 200);

        let snapshot = ledger.snapshot(300);

        assert_eq!(turn_ids(&snapshot), ["t2"]);
        assert_eq!(run(&snapshot, "run-1").generating_turn_id, "t2");
    }

    #[test]
    fn a_single_pass_turn_is_in_flight_but_opens_no_run() {
        let mut ledger = Ledger::default();
        ledger.started(&request("t1", "d-1", EngineClass::SinglePass, ServiceClass::Interactive, ""), "judge", 100);

        let snapshot = ledger.snapshot(150);

        assert_eq!(turn_ids(&snapshot), ["t1"]);
        assert!(snapshot.runs.is_empty());
    }

    #[test]
    fn completing_an_unknown_turn_changes_nothing() {
        let mut ledger = Ledger::default();
        ledger.started(&chat("t1", "run-1"), "r1", 100);
        ledger.completed("never-started", false, 200);

        let snapshot = ledger.snapshot(300);

        assert_eq!(turn_ids(&snapshot), ["t1"]);
        assert_eq!(run(&snapshot, "run-1").phase, RunPhase::Generating as i32);
    }

    #[test]
    fn an_ended_run_is_forgotten() {
        let mut ledger = Ledger::default();
        ledger.started(&chat("t1", "run-1"), "r1", 100);
        ledger.completed("t1", true, 200);
        ledger.ended("run-1");

        assert!(ledger.snapshot(300).runs.is_empty());
    }

    #[test]
    fn runs_idle_for_more_than_30_minutes_are_pruned_but_generating_ones_are_kept() {
        let mut ledger = Ledger::default();
        ledger.started(&chat("t1", "paused"), "r1", 0);
        ledger.completed("t1", true, 0);
        ledger.started(&chat("t2", "recent"), "r1", 0);
        ledger.completed("t2", false, 5 * MINUTE);
        ledger.started(&chat("t3", "generating"), "r1", 0);

        ledger.prune(30 * MINUTE + 1);

        let mut runs: Vec<_> = ledger.snapshot(30 * MINUTE + 1).runs.into_iter().map(|r| r.run_id).collect();
        runs.sort();
        assert_eq!(runs, ["generating", "recent"]);
    }

    #[test]
    fn starting_turns_prunes_idle_runs_once_a_minute() {
        let mut ledger = Ledger::default();
        ledger.started(&chat("t1", "old"), "r1", 0);
        ledger.completed("t1", false, 0);

        ledger.started(&chat("t2", "new"), "r1", 31 * MINUTE);

        let runs: Vec<_> = ledger.runs.keys().cloned().collect();
        assert_eq!(runs, ["new"]);
    }
}
