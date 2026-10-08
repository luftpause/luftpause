use std::env;
use std::time::Duration;

#[derive(Clone, Copy, Debug)]
pub struct EngineConfig {
    pub port: u16,
    pub prefill_per_token: Duration,
    pub decode_per_token: Duration,
    pub tool_rounds: usize,
    pub reply_tokens: usize,
}

impl EngineConfig {
    pub fn from_env() -> Self {
        Self {
            port: read("PORT", 9000),
            prefill_per_token: Duration::from_micros(read("PREFILL_US_PER_TOKEN", 50)),
            decode_per_token: Duration::from_micros(1_000_000 / read::<u64>("DECODE_TOKENS_PER_S", 200).max(1)),
            tool_rounds: read("TOOL_ROUNDS", 3),
            reply_tokens: read("REPLY_TOKENS", 20),
        }
    }
}

fn read<T: std::str::FromStr>(name: &str, default: T) -> T {
    env::var(name).ok().and_then(|v| v.parse().ok()).unwrap_or(default)
}
