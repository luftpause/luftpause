use serde_json::Value;

use crate::config::EngineConfig;

#[derive(Debug, PartialEq)]
pub enum Reply {
    ToolCall { round: usize },
    Text { tokens: usize },
}

#[derive(Debug)]
pub struct Plan {
    pub prompt_tokens: usize,
    pub reply: Reply,
    pub stream: bool,
    pub model: String,
}

pub fn plan(request: &Value, config: &EngineConfig) -> Plan {
    let messages = request["messages"].as_array().map(Vec::as_slice).unwrap_or_default();
    let tool_results = messages.iter().filter(|m| m["role"] == "tool").count();
    let prompt_chars: usize = messages.iter().map(|m| m["content"].as_str().map_or(0, str::len)).sum();

    Plan {
        prompt_tokens: prompt_chars.div_ceil(4).max(1),
        reply: if tool_results < config.tool_rounds {
            Reply::ToolCall { round: tool_results + 1 }
        } else {
            Reply::Text { tokens: config.reply_tokens }
        },
        stream: request["stream"].as_bool().unwrap_or(false),
        model: request["model"].as_str().unwrap_or("fake").to_owned(),
    }
}

impl Reply {
    pub fn completion_tokens(&self) -> usize {
        match self {
            Reply::ToolCall { .. } => 8,
            Reply::Text { tokens } => *tokens,
        }
    }

    pub fn finish_reason(&self) -> &'static str {
        match self {
            Reply::ToolCall { .. } => "tool_calls",
            Reply::Text { .. } => "stop",
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::time::Duration;

    fn config(tool_rounds: usize) -> EngineConfig {
        EngineConfig {
            port: 0,
            prefill_per_token: Duration::ZERO,
            decode_per_token: Duration::ZERO,
            tool_rounds,
            reply_tokens: 5,
        }
    }

    #[test]
    fn calls_a_tool_until_the_rounds_are_used_up() {
        let first = json!({"messages": [{"role": "user", "content": "hi"}]});
        let last = json!({"messages": [
            {"role": "user", "content": "hi"},
            {"role": "tool", "content": "r1"},
            {"role": "tool", "content": "r2"}
        ]});

        assert_eq!(plan(&first, &config(2)).reply, Reply::ToolCall { round: 1 });
        assert_eq!(plan(&last, &config(2)).reply, Reply::Text { tokens: 5 });
    }

    #[test]
    fn prompt_tokens_are_a_quarter_of_the_characters() {
        let request = json!({"messages": [{"role": "user", "content": "abcdefgh"}]});

        assert_eq!(plan(&request, &config(0)).prompt_tokens, 2);
    }
}
