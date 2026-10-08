use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use serde_json::Value;

const ENTRY_LIFETIME: Duration = Duration::from_secs(30 * 60);
const PRUNE_EVERY: Duration = Duration::from_secs(60);

pub fn tool_result_ids(request: &Value) -> Vec<String> {
    let messages = request["messages"].as_array().map(Vec::as_slice).unwrap_or_default();
    messages
        .iter()
        .rev()
        .take_while(|m| m["role"] == "tool")
        .filter_map(|m| m["tool_call_id"].as_str().map(str::to_owned))
        .collect()
}

pub struct RunIndex {
    inner: Mutex<Entries>,
}

struct Entries {
    by_tool_call: HashMap<String, (String, Instant)>,
    pruned_at: Instant,
}

impl RunIndex {
    pub fn new() -> Self {
        Self { inner: Mutex::new(Entries { by_tool_call: HashMap::new(), pruned_at: Instant::now() }) }
    }

    pub fn resolve(&self, tool_call_ids: &[String]) -> Option<String> {
        let entries = self.inner.lock().expect("run index");
        tool_call_ids.iter().find_map(|id| entries.by_tool_call.get(id).map(|(run, _)| run.clone()))
    }

    pub fn record(&self, tool_call_id: &str, run_id: &str) {
        let now = Instant::now();
        let mut entries = self.inner.lock().expect("run index");
        entries.by_tool_call.insert(tool_call_id.to_owned(), (run_id.to_owned(), now));
        if now.duration_since(entries.pruned_at) > PRUNE_EVERY {
            entries.by_tool_call.retain(|_, (_, at)| now.duration_since(*at) < ENTRY_LIFETIME);
            entries.pruned_at = now;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn tool_result_ids_are_the_trailing_tool_messages() {
        let request = json!({"messages": [
            {"role": "user", "content": "hi"},
            {"role": "tool", "tool_call_id": "old"},
            {"role": "assistant", "tool_calls": [{"id": "c1"}, {"id": "c2"}]},
            {"role": "tool", "tool_call_id": "c1"},
            {"role": "tool", "tool_call_id": "c2"}
        ]});

        assert_eq!(tool_result_ids(&request), vec!["c2", "c1"]);
    }

    #[test]
    fn a_first_turn_has_no_tool_results() {
        assert!(tool_result_ids(&json!({"messages": [{"role": "user", "content": "hi"}]})).is_empty());
    }

    #[test]
    fn a_recorded_tool_call_resolves_to_its_run() {
        let index = RunIndex::new();
        index.record("c1", "run-1");

        assert_eq!(index.resolve(&["unknown".into(), "c1".into()]), Some("run-1".into()));
        assert_eq!(index.resolve(&["unknown".into()]), None);
    }
}
