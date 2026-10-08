use serde_json::Value;

#[derive(Debug, Default, Clone, PartialEq)]
pub struct Outcome {
    pub finish_reason: Option<String>,
    pub prompt_tokens: u32,
    pub completion_tokens: u32,
    pub cached_tokens: u32,
    pub tool_call_ids: Vec<String>,
}

#[derive(Debug, Default, PartialEq)]
pub struct Observation {
    pub first_token: bool,
    pub tool_call_ids: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Format {
    Sse,
    Json,
}

impl Format {
    pub fn from_content_type(content_type: &str) -> Self {
        if content_type.starts_with("text/event-stream") { Format::Sse } else { Format::Json }
    }
}

pub struct Observer {
    format: Format,
    buffer: Vec<u8>,
    first_token_seen: bool,
    outcome: Outcome,
}

impl Observer {
    pub fn new(format: Format) -> Self {
        Self { format, buffer: Vec::new(), first_token_seen: false, outcome: Outcome::default() }
    }

    pub fn feed(&mut self, chunk: &[u8]) -> Observation {
        let seen_before = self.first_token_seen;
        let ids_before = self.outcome.tool_call_ids.len();
        self.buffer.extend_from_slice(chunk);
        if self.format == Format::Sse {
            self.drain_lines();
        }
        Observation {
            first_token: !seen_before && self.first_token_seen,
            tool_call_ids: self.outcome.tool_call_ids[ids_before..].to_vec(),
        }
    }

    pub fn finish(mut self) -> Outcome {
        match self.format {
            Format::Sse => {
                self.buffer.push(b'\n');
                self.drain_lines();
            }
            Format::Json => {
                if let Ok(body) = serde_json::from_slice::<Value>(&self.buffer) {
                    self.apply(&body, "message");
                }
            }
        }
        self.outcome
    }

    fn drain_lines(&mut self) {
        while let Some(end) = self.buffer.iter().position(|&b| b == b'\n') {
            let line: Vec<u8> = self.buffer.drain(..=end).collect();
            if let Some(event) = parse_data_line(&line) {
                self.apply(&event, "delta");
            }
        }
    }

    fn apply(&mut self, event: &Value, part: &str) {
        let choice = &event["choices"][0];
        if carries_token(&choice[part]) {
            self.first_token_seen = true;
        }
        let fragments = choice[part]["tool_calls"].as_array().map(Vec::as_slice).unwrap_or_default();
        let ids = fragments.iter().filter_map(|c| c["id"].as_str()).filter(|id| !id.is_empty());
        self.outcome.tool_call_ids.extend(ids.map(str::to_owned));
        if let Some(reason) = choice["finish_reason"].as_str() {
            self.outcome.finish_reason = Some(reason.to_owned());
        }
        let usage = &event["usage"];
        if usage.is_object() {
            self.outcome.prompt_tokens = count(&usage["prompt_tokens"]);
            self.outcome.completion_tokens = count(&usage["completion_tokens"]);
            self.outcome.cached_tokens = count(&usage["prompt_tokens_details"]["cached_tokens"]);
        }
    }
}

fn parse_data_line(line: &[u8]) -> Option<Value> {
    let text = std::str::from_utf8(line).ok()?.trim_end();
    let payload = text.strip_prefix("data:")?.trim_start();
    if payload == "[DONE]" { None } else { serde_json::from_str(payload).ok() }
}

fn carries_token(part: &Value) -> bool {
    part["content"].as_str().is_some_and(|c| !c.is_empty())
        || part["tool_calls"].as_array().is_some_and(|calls| !calls.is_empty())
}

fn count(value: &Value) -> u32 {
    value.as_u64().unwrap_or(0) as u32
}

#[cfg(test)]
mod tests {
    use super::*;

    const STREAM: &str = concat!(
        "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n",
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_7\"}]}}]}\n\n",
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{}\"}}]}}]}\n\n",
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n",
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":8,\"prompt_tokens_details\":{\"cached_tokens\":4}}}\n\n",
        "data: [DONE]\n\n"
    );

    #[test]
    fn first_token_and_tool_call_ids_are_reported_by_the_chunk_that_carries_them() {
        let mut observer = Observer::new(Format::Sse);
        let bytes = STREAM.as_bytes();
        let role_only_end = STREAM.find("\n\n").unwrap() + 2;

        assert_eq!(observer.feed(&bytes[..role_only_end]), Observation::default());
        let rest = observer.feed(&bytes[role_only_end..]);

        assert!(rest.first_token);
        assert_eq!(rest.tool_call_ids, vec!["call_7"]);
    }

    #[test]
    fn lines_split_across_chunks_are_reassembled() {
        let mut observer = Observer::new(Format::Sse);
        for piece in STREAM.as_bytes().chunks(7) {
            observer.feed(piece);
        }

        let outcome = observer.finish();

        assert_eq!(outcome.finish_reason.as_deref(), Some("tool_calls"));
        assert_eq!((outcome.prompt_tokens, outcome.completion_tokens, outcome.cached_tokens), (12, 8, 4));
        assert_eq!(outcome.tool_call_ids, vec!["call_7"]);
    }

    #[test]
    fn json_bodies_are_read_at_the_end() {
        let mut observer = Observer::new(Format::Json);
        let body = br#"{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_9"}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}"#;

        assert_eq!(observer.feed(body), Observation::default());
        let outcome = observer.finish();

        assert_eq!(outcome.finish_reason.as_deref(), Some("tool_calls"));
        assert_eq!(outcome.tool_call_ids, vec!["call_9"]);
        assert_eq!(outcome.prompt_tokens, 3);
    }
}
