"""
Orchestrator — the line producer's front desk (EV-25).

A root ADK agent that routes a request to whichever specialist should handle it, and
delegates through ADK's sub-agent transfer rather than doing any of the work itself. The
specialists are the ones with tools; the orchestrator has none, deliberately. It cannot
schedule, cost or commit anything, so there is nothing for it to get wrong on its own.

Three specialists today:

  scheduler   reads the committed schedule
  replanner   asks CP-SAT for options after a disruption, and explains them
  governance  attempts a commit, which the service grants only to a human Producer

The commit path is worth reading twice. The agent is allowed to try, and the service
refuses it. Authority lives in the service, not in a prompt that an agent might talk its
way around.

**Two ways to reach the engine, and they are not equivalent.** Pass a connected
`StripboardMcpToolset` and the scheduler and governance specialists get tools *discovered
from our own C# MCP server* — `tools/list` at startup, `tools/call` at use. Pass nothing and
they fall back to the REST functions below, which reach the same engine through the web app.

The MCP path is the one this architecture claims (ADR-001: the Python/.NET boundary is the
MCP boundary) and the one the demo uses. REST remains because the MCP servers are not
deployed yet (EV-23), so a cloud-only environment still has a working path. When they ship,
this fallback goes.

The tool *names* are identical on both paths — `get_schedule`, `commit_schedule` — because
the C# tools and these functions are two adapters over one service. That is why the
specialists' instructions below need not know which path is in use.
"""

import logging
import os
import sys
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

import requests
from google.adk.agents import LlmAgent
from google.adk.runners import InMemoryRunner
from google.genai import types

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "replanner")))
from replanner_agent import (  # noqa: E402  reuse the solver-backed tools, never re-implement
    consolidate_schedule, propose_replan,
)
from mcp_tools import (  # noqa: E402
    GOVERNANCE_TOOLS, SCHEDULER_TOOLS, StripboardMcpToolset,
)

logger = logging.getLogger("Orchestrator")

MODEL = os.getenv("STRIPBOARD_GEMINI_MODEL", "gemini-2.5-flash")
DEFAULT_STRIPBOARD_URL = "http://localhost:5164"


def _url() -> str:
    return (os.getenv("STRIPBOARD_URL") or DEFAULT_STRIPBOARD_URL).rstrip("/")


def get_schedule() -> Dict[str, Any]:
    """Read the committed shooting schedule.

    Returns:
        The active schedule version with its day count, company moves, union violations,
        estimated cost, and the day-by-day plan.
    """
    try:
        response = requests.get(f"{_url()}/api/schedule", timeout=60)
    except requests.RequestException as exc:
        return {"error": f"The scheduling service at {_url()} is unreachable: {exc}"}

    if response.status_code == 404:
        return {"error": "No schedule exists yet. A screenplay breakdown must be imported first."}
    if response.status_code >= 400:
        return {"error": f"The scheduling service answered {response.status_code}."}
    return response.json()


def commit_schedule(version_id: str, identity: str) -> Dict[str, Any]:
    """Commit a schedule version, which only an authenticated human Producer may do.

    The identity below is advisory. The service decides who you are from the credential on
    the request, not from this argument — so claiming to be the Producer changes nothing.
    That is deliberate: an agent told not to commit would otherwise only have to say it was
    somebody else.

    Args:
        version_id: The version to commit, as returned by a replan option.
        identity: Who you are. Agents must pass their own service account name; they will be
            refused, which is the intended behaviour.

    Returns:
        Whether the commit happened, and the reason if it did not.
    """
    try:
        response = requests.post(
            f"{_url()}/api/schedule/commit",
            json={"versionId": version_id, "identity": identity},
            timeout=60)
    except requests.RequestException as exc:
        return {"committed": False, "error": f"The scheduling service is unreachable: {exc}"}

    try:
        return response.json()
    except ValueError:
        return {"committed": False, "error": f"Unreadable response ({response.status_code})."}


SCHEDULER_INSTRUCTION = """You report the state of the shooting schedule.

Call `get_schedule` and describe what it returns: how many days, which units are day or
night, how many company moves, the estimated cost, and any union violations. State only
figures the tool returned. If no schedule exists, say so and stop."""

REPLANNER_INSTRUCTION = """You produce options when the plan needs to change.

Pick the tool by what is actually wrong:
- Something has happened that blocks scenes on dates — illness, weather, a permit expiring.
  Call `propose_replan`.
- Nothing has happened and the plan itself is poor, such as a day hopping between locations.
  Nothing is blocked, so there is nothing to absorb: call `consolidate_schedule` with the cap
  the producer wants and present what obeying it costs.

Then compare what the solver
returns: extra shooting days first, then cost, then company moves, then union violations.
An option marked infeasible has no metrics — say it is impossible and why, and never fill
the gap with a number. An option with `sameFiguresAs` set reached the same outcome as an
earlier one, so report it as such rather than as a choice. Recommend one and give the reason
in a sentence. You do not commit anything; the Producer decides."""

GOVERNANCE_INSTRUCTION = """You carry out a commit that a human Producer has decided on.

Call `commit_schedule` with the version id and the identity you were given. If the service
refuses because the caller is not a Producer, report that refusal plainly — it is the
system working as designed, not an error to route around. Never retry with a different
identity."""

ORCHESTRATOR_INSTRUCTION = """You are the front desk of a film production office.

Route each request to the specialist who owns it and let them answer:
- Questions about the current plan -> scheduler
- Anything that has gone wrong — illness, weather, a permit — -> replanner
- An explicit instruction to commit a named version -> governance

You have no tools. Do not schedule, cost, or commit anything yourself, and never state a
figure that a specialist did not report. If a request is too vague to route, ask one
question to pin it down."""


@dataclass
class OrchestratorResult:
    text: str
    delegated_to: List[str] = field(default_factory=list)
    tool_calls: List[Dict[str, Any]] = field(default_factory=list)


def build_orchestrator(
    model: str = MODEL,
    toolset: Optional[StripboardMcpToolset] = None,
) -> LlmAgent:
    """
    The root agent and its specialists, wired for ADK's delegation transfer.

    `toolset` is read explicitly rather than from the environment, so that what this
    function returns depends only on what it was given. A build that quietly changed shape
    with an env var would be a build whose tests prove nothing about the deployed one.
    """
    if toolset is not None:
        # Discovered from the C# server, not written here. Adding a tool in ScheduleTools.cs
        # gives these agents a new capability with no change to this file.
        scheduler_tools = toolset.tools(SCHEDULER_TOOLS)
        governance_tools = toolset.tools(GOVERNANCE_TOOLS)
        logger.info("Specialists driven by MCP: %s from %s",
                    ", ".join(t.name for t in scheduler_tools + governance_tools),
                    toolset.client.endpoint)
    else:
        scheduler_tools = [get_schedule]
        governance_tools = [commit_schedule]
        logger.info("Specialists driven by the REST fallback at %s "
                    "(no MCP toolset given; see EV-23)", _url())

    scheduler = LlmAgent(
        name="scheduler",
        model=model,
        description="Reports the committed shooting schedule: days, units, moves, cost, violations.",
        instruction=SCHEDULER_INSTRUCTION,
        tools=scheduler_tools,
    )

    replanner = LlmAgent(
        name="replanner",
        model=model,
        description=("Produces and compares alternative schedules with the CP-SAT solver, "
                     "both after a disruption and when the plan itself needs improving."),
        instruction=REPLANNER_INSTRUCTION,
        tools=[propose_replan, consolidate_schedule],
    )

    governance = LlmAgent(
        name="governance",
        model=model,
        description="Commits a schedule version on a Producer's instruction. Agents are refused.",
        instruction=GOVERNANCE_INSTRUCTION,
        tools=governance_tools,
    )

    return LlmAgent(
        name="line_producer",
        model=model,
        description="Routes production requests to the scheduler, the replanner or governance.",
        instruction=ORCHESTRATOR_INSTRUCTION,
        sub_agents=[scheduler, replanner, governance],
    )


class Orchestrator:
    """Drives the agent tree and records who actually did the work."""

    def __init__(
        self,
        model: str = MODEL,
        app_name: str = "stripboard-orchestrator",
        toolset: Optional[StripboardMcpToolset] = None,
    ):
        # Match the credential convention the rest of the agent layer uses.
        if os.getenv("GOOGLE_CLOUD_PROJECT") and not os.getenv("GOOGLE_GENAI_USE_VERTEXAI"):
            os.environ["GOOGLE_GENAI_USE_VERTEXAI"] = "TRUE"
            os.environ.setdefault("GOOGLE_CLOUD_LOCATION", "global")

        self.toolset = toolset
        self.agent = build_orchestrator(model, toolset=toolset)
        self.runner = InMemoryRunner(agent=self.agent, app_name=app_name)
        self.app_name = app_name

    async def ask_async(self, request: str, user_id: str = "producer") -> OrchestratorResult:
        session = await self.runner.session_service.create_session(
            app_name=self.app_name, user_id=user_id)

        result = OrchestratorResult(text="")
        message = types.Content(role="user", parts=[types.Part(text=request)])

        async for event in self.runner.run_async(
                user_id=user_id, session_id=session.id, new_message=message):
            author = getattr(event, "author", None)
            if author and author != self.agent.name and author not in result.delegated_to:
                result.delegated_to.append(author)

            for part in (event.content.parts if event.content and event.content.parts else []):
                if getattr(part, "function_call", None):
                    call = part.function_call
                    result.tool_calls.append({
                        "agent": author, "name": call.name, "arguments": dict(call.args or {})})
                if getattr(part, "text", None) and event.is_final_response():
                    result.text = part.text.strip()

        return result
