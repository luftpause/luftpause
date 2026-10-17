"""An own-loop agent on the openai package: two lookups, then an answer, every turn through the luftpause proxy.
Run: pip install -r requirements.txt && python agent.py   (luftpause up on http://localhost:8080)
Headers: x-luftpause-run-id (one per run), x-luftpause-class, x-luftpause-caller."""

import json
import uuid

from openai import OpenAI

BASE_URL = "http://localhost:8080/v1"
MODEL = "fake"
TASK = "Which city has more people, Berlin or Vienna? Look up both, then answer in one sentence."

POPULATION = {"berlin": 3_878_000, "vienna": 2_005_000}

TOOLS = [
    {
        "type": "function",
        "function": {
            "name": "lookup",
            "description": "Population of a city.",
            "parameters": {
                "type": "object",
                "properties": {"city": {"type": "string"}},
                "required": ["city"],
            },
        },
    }
]


def lookup(city: str = "") -> str:
    population = POPULATION.get(city.strip().lower())
    return f"{city}: {population} people" if population else f"{city}: unknown"


def run_tool(call) -> dict:
    arguments = json.loads(call.function.arguments or "{}")
    return {"role": "tool", "tool_call_id": call.id, "content": lookup(str(arguments.get("city", "")))}


def client_for(run_id: str) -> OpenAI:
    return OpenAI(
        base_url=BASE_URL,
        api_key="unused",
        default_headers={
            "x-luftpause-run-id": run_id,
            "x-luftpause-class": "interactive",
            "x-luftpause-caller": "examples/openai-sdk",
        },
    )


def run(client: OpenAI, task: str, max_tool_calls: int = 2) -> str:
    messages = [{"role": "user", "content": task}]
    tool_calls = 0
    while True:
        offer_tools = tool_calls < max_tool_calls
        reply = client.chat.completions.create(
            model=MODEL,
            messages=messages,
            max_tokens=256,
            **({"tools": TOOLS, "parallel_tool_calls": False} if offer_tools else {}),
        ).choices[0].message
        if not reply.tool_calls:
            return reply.content or ""
        messages.append(reply.model_dump(exclude_none=True))
        for call in reply.tool_calls:
            result = run_tool(call)
            print(f"tool {call.function.name}({call.function.arguments}) -> {result['content']}")
            messages.append(result)
        tool_calls += len(reply.tool_calls)


if __name__ == "__main__":
    print(run(client_for(str(uuid.uuid4())), TASK))
