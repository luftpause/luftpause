use serde_json::{Value, json};

use crate::script::{Plan, Reply};

pub fn usage(plan: &Plan) -> Value {
    json!({
        "prompt_tokens": plan.prompt_tokens,
        "completion_tokens": plan.reply.completion_tokens(),
        "total_tokens": plan.prompt_tokens + plan.reply.completion_tokens(),
        "prompt_tokens_details": { "cached_tokens": 0 }
    })
}

pub fn tool_call(id: &str, round: usize) -> Value {
    json!({
        "index": 0,
        "id": format!("call_{id}_{round}"),
        "type": "function",
        "function": { "name": "lookup", "arguments": format!("{{\"round\":{round}}}") }
    })
}

pub fn completion(id: &str, plan: &Plan) -> Value {
    let message = match plan.reply {
        Reply::ToolCall { round } => json!({ "role": "assistant", "content": null, "tool_calls": [tool_call(id, round)] }),
        Reply::Text { tokens } => json!({ "role": "assistant", "content": "tok ".repeat(tokens) }),
    };
    json!({
        "id": id,
        "object": "chat.completion",
        "model": plan.model,
        "choices": [{ "index": 0, "message": message, "finish_reason": plan.reply.finish_reason() }],
        "usage": usage(plan)
    })
}

pub fn chunk(id: &str, model: &str, delta: Value, finish_reason: Option<&str>) -> Value {
    json!({
        "id": id,
        "object": "chat.completion.chunk",
        "model": model,
        "choices": [{ "index": 0, "delta": delta, "finish_reason": finish_reason }]
    })
}

pub fn usage_chunk(id: &str, plan: &Plan) -> Value {
    json!({
        "id": id,
        "object": "chat.completion.chunk",
        "model": plan.model,
        "choices": [],
        "usage": usage(plan)
    })
}

pub fn sse(event: &Value) -> String {
    format!("data: {event}\n\n")
}

pub const SSE_DONE: &str = "data: [DONE]\n\n";

pub fn classification(id: &str, model: &str, inputs: &[String]) -> Value {
    let data: Vec<Value> = inputs
        .iter()
        .enumerate()
        .map(|(index, text)| {
            let positive = if text.len() % 2 == 0 { 0.8 } else { 0.3 };
            json!({
                "index": index,
                "label": if positive > 0.5 { "yes" } else { "no" },
                "probs": [positive, 1.0 - positive],
                "num_classes": 2
            })
        })
        .collect();
    let prompt_tokens: usize = inputs.iter().map(|t| t.len().div_ceil(4)).sum();
    json!({
        "id": id,
        "object": "list",
        "model": model,
        "data": data,
        "usage": { "prompt_tokens": prompt_tokens, "total_tokens": prompt_tokens, "completion_tokens": 0 }
    })
}
