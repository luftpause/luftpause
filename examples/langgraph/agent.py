"""An own-loop agent on langgraph + langchain-openai: two lookups, then an answer, every turn through the luftpause proxy.
Run: pip install -r requirements.txt && python agent.py   (luftpause up on http://localhost:8080)
Headers: x-luftpause-run-id (one per run), x-luftpause-class, x-luftpause-caller."""

import uuid

from langchain_core.tools import tool
from langchain_openai import ChatOpenAI
from langgraph.graph import START, MessagesState, StateGraph
from langgraph.prebuilt import ToolNode, tools_condition

BASE_URL = "http://localhost:8080/v1"
MODEL = "fake"
TASK = "Which city has more people, Berlin or Vienna? Look up both, then answer in one sentence."

POPULATION = {"berlin": 3_878_000, "vienna": 2_005_000}


@tool
def lookup(city: str) -> str:
    """Population of a city."""
    population = POPULATION.get(city.strip().lower())
    return f"{city}: {population} people" if population else f"{city}: unknown"


def model_for(run_id: str) -> ChatOpenAI:
    return ChatOpenAI(
        base_url=BASE_URL,
        api_key="unused",
        model=MODEL,
        max_tokens=256,
        default_headers={
            "x-luftpause-run-id": run_id,
            "x-luftpause-class": "interactive",
            "x-luftpause-caller": "examples/langgraph",
        },
    )


def graph_for(model: ChatOpenAI):
    bound = model.bind_tools([lookup], parallel_tool_calls=False)
    graph = StateGraph(MessagesState)
    graph.add_node("model", lambda state: {"messages": [bound.invoke(state["messages"])]})
    graph.add_node("tools", ToolNode([lookup]))
    graph.add_edge(START, "model")
    graph.add_conditional_edges("model", tools_condition)
    graph.add_edge("tools", "model")
    return graph.compile()


if __name__ == "__main__":
    final = graph_for(model_for(str(uuid.uuid4()))).invoke({"messages": [("user", TASK)]}, {"recursion_limit": 8})
    print(final["messages"][-1].content)
