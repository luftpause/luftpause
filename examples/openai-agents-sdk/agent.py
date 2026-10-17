"""An own-loop agent on the openai-agents package: two lookups, then an answer, every turn through the luftpause proxy.
Run: pip install -r requirements.txt && python agent.py   (luftpause up on http://localhost:8080)
Headers: x-luftpause-run-id (one per run), x-luftpause-class, x-luftpause-caller."""

import asyncio
import uuid

from agents import Agent, ModelSettings, OpenAIChatCompletionsModel, Runner, function_tool, set_tracing_disabled
from openai import AsyncOpenAI

BASE_URL = "http://localhost:8080/v1"
MODEL = "fake"
TASK = "Which city has more people, Berlin or Vienna? Look up both, then answer in one sentence."

POPULATION = {"berlin": 3_878_000, "vienna": 2_005_000}


@function_tool
def lookup(city: str) -> str:
    """Population of a city."""
    population = POPULATION.get(city.strip().lower())
    return f"{city}: {population} people" if population else f"{city}: unknown"


def client_for(run_id: str) -> AsyncOpenAI:
    return AsyncOpenAI(
        base_url=BASE_URL,
        api_key="unused",
        default_headers={
            "x-luftpause-run-id": run_id,
            "x-luftpause-class": "interactive",
            "x-luftpause-caller": "examples/openai-agents-sdk",
        },
    )


def agent_for(client: AsyncOpenAI) -> Agent:
    return Agent(
        name="lookup-agent",
        instructions="Use the lookup tool once per city, then answer.",
        tools=[lookup],
        model=OpenAIChatCompletionsModel(model=MODEL, openai_client=client),
        model_settings=ModelSettings(max_tokens=256, parallel_tool_calls=False),
    )


async def main() -> None:
    set_tracing_disabled(True)
    result = await Runner.run(agent_for(client_for(str(uuid.uuid4()))), TASK, max_turns=6)
    print(result.final_output)


if __name__ == "__main__":
    asyncio.run(main())
