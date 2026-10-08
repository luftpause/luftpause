# luftpause

**async/await for LLM agents. Nothing blocks, so the GPUs never wait on a tool.**

An agent spends most of its life waiting — on a tool, on a sub-agent, on a slow
API. In most stacks that wait holds something: a GPU slot, a thread, the caller.
luftpause turns every wait into an `await`:

- **A run waiting on a tool holds no GPU slot.** The next ready turn takes it.
- **A caller that spawns sub-agents keeps working.** `spawn` returns a handle at
  once; `await` collects results when the caller wants them.
- **The pool is scheduled per turn, not per agent.** Interactive turns go ahead of
  batch turns; each run returns to the replica that holds its context.

Measured on one 8 GB laptop GPU:

| | Result |
|---|---|
| Fan-out, 16 sub-agents | **5.3× throughput** — 23 s instead of 120 s one at a time |
| Interactive agents under a batch flood | **2.4× throughput**, p50 16 s instead of 34 s; total throughput unchanged |
| Same flood vs SGLang's own priority scheduling | **+46 %** interactive throughput (14.2 vs 9.7 runs/min) |
| Claude Code as the client | spawns and awaits on its own, unprompted; 70B sub-agents finished in one round with no failed tool calls |

## Try it

```bash
docker compose up -d --build        # fake engines, no GPU needed; first build takes a few minutes
claude mcp add --transport http --scope local luftpause http://localhost:7000/mcp
```

Claude Code now has `spawn`, `await`, `status`, `cancel` and `tools`. The fake
engines answer with filler — enough to see the fan-out. For real answers put a
model behind it: SGLang on your GPU (`compose.testbed.yaml`) or any
OpenAI-compatible endpoint (`compose.runpod.yaml` shows a hosted vLLM pod).

## How it differs

MCP servers that delegate a sub-task to a local model exist; routers that place
requests on a GPU pool exist. luftpause sits between them: it owns the agent loop,
so it sees each turn and each tool call, and schedules the turns of every run on
one shared pool.

The gains come from not blocking, not from routing: routing alone already matches
SGLang's cache-aware router (below).

## Two ways in

- **MCP runtime** — an agent such as Claude Code connects over MCP and gets
  `spawn` / `await`. luftpause runs each sub-agent's loop: LLM turns on the pool,
  tool calls to upstream MCP servers.
- **OpenAI-compatible proxy** — an agent that keeps its own loop points its
  `base_url` at luftpause and gets the same admission and routing.

## Architecture

```
Claude Code / any MCP client ──MCP──► runtime (.NET): spawn / await / status / cancel / decide
                                         │  sub-agent loops        └── tools ──MCP──► tool servers
                                         ▼
agents (own loop) ──HTTP/SSE──►  dataplane (Rust)  ──proxied turns──► engines (SGLang, vLLM, …)
                                   │   ▲
                     Report(events)│   │ Admit → Route | Reject | Refuse
                                   ▼   │
                              scheduler (.NET)  decide(state, event) → (state, actions)
```

- **runtime** (`runtime/`) — MCP server. Each spawned run is a .NET `Task` running
  the agent loop; tool calls go to the configured MCP servers.
- **dataplane** (`dataplane/crates/dataplane`) — tokio/axum proxy. Resolves the
  run (header or tool-call ids), asks the scheduler where the turn goes, streams
  the engine's response through unchanged.
- **scheduler** (`scheduler/`) — the decision core is pure functions over
  immutable state (`Luftpause.Scheduling`); the gRPC host executes the actions.
- **test kit** — `fake-engine` (OpenAI + `/classify` with simulated latency),
  `fake-mcp` (a `lookup` and a slow `deep_search` tool), `tools/loadgen` (agents,
  sub-agent fan-out over MCP, closed-loop throughput runs).

## What it does

| Capability | How |
|---|---|
| `spawn` / `await` over MCP | `spawn(task, model?, serviceClass?, system?, maxToolCalls?, tools?)` returns a handle at once; `await(handles, all\|any, timeout)` collects results with `toolCalls` / `failedToolCalls`; also `tools`, `status`, `cancel`, `decide` |
| Tools apart from turns | a run waiting on a tool holds no turn slot; tool calls run in the runtime against MCP servers over HTTP or stdio |
| Admission by engine load | turns go to an engine while it reports no waiting queue (SGLang and vLLM `/metrics`); once it queues, luftpause caps its turns at what the engine runs and holds the rest; engines that report no load are not capped |
| Service classes | batch turns leave room on an engine for each interactive run paused on a tool there, since it will come back; turns held by luftpause go out `interactive` first, then by arrival; 429 after 60 s waiting |
| Routers as backends | a replica can be a router (e.g. the SGLang router) that picks the engine; its load is read from the engines behind it (`LoadFrom`) |
| Affinity | a run's next turn goes back to the replica that served its last one, where its prompt prefix is cached |
| Routing by model | endpoints declare the models they serve; a turn only goes to a replica serving its model; an unknown model gets 404 at once |
| Hosted endpoints | any OpenAI-compatible API (vLLM on RunPod, OpenAI, OpenRouter, …) as a replica: API key from an environment variable, health path per endpoint, https |
| Health | `/health` (or any path per endpoint) probed every 2 s; unhealthy replicas receive no turns |
| Tool-call hygiene | arguments coerced to the schema's scalar types, validated, and rejected with the expected shape instead of a silent bad call; calls written as text are recovered |
| Run identity | `x-luftpause-run-id`, or the `tool_call_id`s an agent echoes back |
| Single-pass engines | `POST /v1/decide` (vLLM `/classify` shape) on separate replicas, outside run state |

## Running it

```bash
docker compose up -d --build                     # scheduler, dataplane, runtime, fake engines, fake MCP tools
dotnet run --project tools/loadgen -- --spawn 10 # fan 10 sub-agents out over MCP and await them
```

On one GPU, two SGLang replicas of Llama-3.2-1B FP8 (the test bed used below):

```bash
docker compose -f compose.yaml -f compose.testbed.yaml up -d --build scheduler dataplane runtime fake-mcp sglang-a sglang-b
```

A hosted vLLM pod as an extra replica (key and pod id from the environment):

```bash
RUNPOD_POD_ID=<pod-id> RUNPOD_KEY=<key> docker compose -f compose.yaml -f compose.testbed.yaml -f compose.runpod.yaml up -d scheduler runtime
```

## Measurements

One laptop, RTX 4060 Laptop 8 GB, two SGLang replicas of Llama-3.2-1B-Instruct FP8
unless stated. Repeats are noted per table; differences inside the spread are
noise.

### Fan-out: `spawn` × 16 + `await all` vs one sub-agent at a time (3 repeats)

16 sub-agents, ~2.8k-token tasks, `lookup` tool fixed at 2 s, up to 3 calls each.

| | Time for all 16 | vs sequential |
|---|---|---|
| sequential (spawn, await, next) | 120.5 s | — |
| luftpause fan-out | 22.9 s | **5.3×** |
| fan-out straight to the SGLang router | 21.5 s | 5.6× |

Per sub-agent, fan-out and router take the same time (p50 15–22 s); the 1.4 s
difference is inside the run-to-run spread.

### Service classes under a batch flood (3 repeats)

24 batch sub-agents flooding the replicas, 4 interactive ones beside them, 3
minutes per run. SGLang runs with `--enable-priority-scheduling` in every row.
Measured with 8 turns per engine, 2 of them kept for interactive runs.

| | Interactive runs/min | Interactive run time p50 / p90 | Batch runs/min | Total runs/min |
|---|---|---|---|---|
| class-blind (first come, first served) | 6.0 | 34.3 s / 48.0 s | 37.6 | 43.5 |
| SGLang priority only (interactive 100, batch 0, pre-emption on; no luftpause slot limit) | 9.7 | 23.8 s / 31.7 s | 34.4 | 44.1 |
| **luftpause classes** | **14.2** | **16.1 s / 23.0 s** | 31.0 | 45.2 |
| luftpause classes + SGLang priority | 13.7 | 16.8 s / 25.2 s | 31.8 | 45.5 |

Interactive throughput is 2.4× class-blind and 1.46× SGLang priority; batch gives
up 18 %, total runs/min do not drop. Engine priority on top changes nothing
measurable.

### Claude Code as the client

Told to fan out six river look-ups, Claude Code spawned six runs and awaited them
in one call: 12.5 s for the whole fan-out including its own round trips, against
36 s for the same runs back to back.

In a fresh session, asked only to "research these six rivers using the local
sub-agents", it listed the sub-agent tools, restricted the runs to the Wikipedia
tools, spawned six, awaited them in one call and re-spawned the weak answers.
Tools: [wikipedia-mcp](https://pypi.org/project/wikipedia-mcp/) over stdio.

| Sub-agents | Claude's re-spawns | Tool calls rejected | Outcome |
|---|---|---|---|
| 1B local, before argument coercion | 4 | 38 of 46 | answers mostly from model memory |
| 3B local, with coercion | 2 | 0 of 20 | lengths and mouths right, country lists unreliable |
| **Llama 3.3 70B AWQ on a RunPod vLLM pod** | **0** | **0 of 6** | all six from Wikipedia in one round (9.3 s) |

The 70B pod sat in the same pool as the local 3B replica: turns asking for
`llama-3.3-70b` went to the pod over https with its API key, everything else stayed
local. `failedToolCalls` in `await` tells the caller when an answer did not come
from tools.

### Routing (N = 8 sub-agents in flight, 4 repeats)

| | Runs/min | Prompt tokens from cache |
|---|---|---|
| luftpause affinity | 30.1 ± 1.7 | 44.6 % |
| SGLang router, `cache_aware` | 30.1 ± 0.5 | 44.6 % |
| SGLang router, `round_robin` | 28.2 ± 0.5 | 25.7 % |

Affinity equals the SGLang router's cache-aware routing and beats round-robin by
7–15 % at moderate load; at 16–24 runs in flight the caches churn and policy stops
mattering.

### Single-pass (vLLM classifier, RoBERTa-base)

| Load | Latency at the data plane |
|---|---|
| one call at a time | p50 20.6 ms |
| 20 concurrent agents | p50 85 ms, p99 180 ms |

## License

[Elastic License 2.0](LICENSE): free to use, modify and redistribute, including inside your company; you may not offer luftpause to others as a hosted or managed service.
