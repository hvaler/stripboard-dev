"""
Replanner — a Google ADK agent that explains schedule options it did not invent (EV-24).

This file used to return two hardcoded proposals with the literal figures $1,500 and
$8,500, and called the swap of two list elements "planning". Nothing here computes a
schedule any more: the CP-SAT solver does, behind `POST /api/replan`, and the agent's only
job is to choose which disruption to model and to explain the result a producer is looking
at.

That division is the project's rule applied to the agent layer — the LLM formulates, the
solver decides, a human approves. It is enforced structurally rather than by instruction:
the agent has no arithmetic to do because every number arrives from the tool.
"""

import json
import logging
import os
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

import requests
from google.adk.agents import LlmAgent
from google.adk.runners import InMemoryRunner
from google.genai import types

logger = logging.getLogger("ReplannerAgent")

DEFAULT_STRIPBOARD_URL = "http://localhost:5164"
MODEL = os.getenv("STRIPBOARD_GEMINI_MODEL", "gemini-2.5-flash")

INSTRUCTION = """You are a replanner working for a 1st Assistant Director.

Two different things can be wrong, and they need different tools:

- **Something has happened** — illness, weather, a permit — that blocks scenes on dates.
  Call `propose_replan`.
- **Nothing has happened; the plan itself is poor** — a day hopping between locations, for
  instance. Nothing is blocked, so there is nothing to absorb. Call `consolidate_schedule`
  with the cap the producer wants, and present what obeying it costs.

Then explain the options the solver returns.

Rules:
- Every figure you state must come from the tool result. You have no way to compute a
  schedule and must never estimate one; if the tool did not return a number, do not give it.
- Compare the options on what a producer decides with: extra shooting days first, then cost,
  then company moves, then union violations.
- Say plainly when an option is infeasible, and why the tool said so.
- An option with `sameFiguresAs` set matched an earlier one on every figure. Report it as the
  same outcome reached another way — do not present it as an alternative worth weighing.
- Recommend one option and give the reason in a sentence. The producer commits it, not you.
- Dates are ISO (YYYY-MM-DD). Trigger types are CastUnavailability, WeatherAlert,
  PermitExpiration or Manual."""


@dataclass
class ReplanResult:
    text: str
    tool_calls: List[Dict[str, Any]] = field(default_factory=list)
    options: List[Dict[str, Any]] = field(default_factory=list)


class StripboardUnavailableError(RuntimeError):
    """The scheduling service could not be reached or refused the request."""


def _stripboard_url() -> str:
    return (os.getenv("STRIPBOARD_URL") or DEFAULT_STRIPBOARD_URL).rstrip("/")


def propose_replan(
    trigger_type: str,
    start_date: str,
    duration_days: int,
    person_name: Optional[str] = None,
    location_name: Optional[str] = None,
    description: Optional[str] = None,
) -> Dict[str, Any]:
    """Ask the CP-SAT solver for alternative schedules that absorb a disruption.

    Args:
        trigger_type: CastUnavailability, WeatherAlert, PermitExpiration or Manual.
        start_date: First affected date, ISO format (YYYY-MM-DD).
        duration_days: How many days the disruption lasts.
        person_name: Cast member affected, for CastUnavailability.
        location_name: Location affected, for WeatherAlert or PermitExpiration.
        description: What happened, in one sentence.

    Returns:
        The disruption as recorded, and one option per replanning strategy with its
        schedule metrics and the cost delta against the committed schedule.
    """
    payload = {
        "triggerType": trigger_type,
        "startDate": start_date,
        "durationDays": duration_days,
        "personName": person_name,
        "locationName": location_name,
        "description": description,
    }

    try:
        response = requests.post(f"{_stripboard_url()}/api/replan", json=payload, timeout=120)
    except requests.RequestException as exc:
        raise StripboardUnavailableError(
            f"The scheduling service at {_stripboard_url()} is unreachable: {exc}") from exc

    if response.status_code >= 400:
        # Hand the reason back rather than a generic failure: "no cast member named X"
        # is something the model can act on.
        try:
            detail = response.json()
        except ValueError:
            detail = {"error": response.text[:300]}
        return {"error": detail.get("error", "The scheduling service rejected the request."),
                **{k: v for k, v in detail.items() if k != "error"}}

    return response.json()


def consolidate_schedule(max_locations_per_day: int) -> Dict[str, Any]:
    """Re-solve with a hard cap on how many locations one shooting day may visit.

    Use this when the problem is the schedule's quality rather than a disruption — a day
    that hops between several locations spends its light travelling. This does not absorb
    anything; it prices a trade. Obeying the cap usually costs shooting days, and the tool
    returns how many.

    Args:
        max_locations_per_day: The most locations a single day may visit. Two means one
            company move; one means none at all.

    Returns:
        The committed schedule as it stands, and the consolidated alternative with the
        difference between them. Both figures come from separate CP-SAT runs.
    """
    try:
        response = requests.post(
            f"{_stripboard_url()}/api/schedule/consolidate",
            json={"maxLocationsPerDay": max_locations_per_day}, timeout=180)
    except requests.RequestException as exc:
        raise StripboardUnavailableError(
            f"The scheduling service at {_stripboard_url()} is unreachable: {exc}") from exc

    if response.status_code >= 400:
        try:
            detail = response.json()
        except ValueError:
            detail = {"error": response.text[:300]}
        return {"error": detail.get("error", "The scheduling service rejected the request."),
                **{k: v for k, v in detail.items() if k != "error"}}

    return response.json()


class ReplannerAgent:
    """Thin wrapper so the demo harness and tests can drive the ADK agent synchronously."""

    def __init__(self, model: str = MODEL, app_name: str = "stripboard-replanner"):
        # The breakdown agent's client treats "a GCP project is configured" as "use Vertex
        # AI". ADK does not infer that, and fails asking for an API key instead. Aligning
        # the two here means one set of credentials works for every agent in the project.
        if os.getenv("GOOGLE_CLOUD_PROJECT") and not os.getenv("GOOGLE_GENAI_USE_VERTEXAI"):
            os.environ["GOOGLE_GENAI_USE_VERTEXAI"] = "TRUE"
            os.environ.setdefault("GOOGLE_CLOUD_LOCATION", "global")

        self.agent = LlmAgent(
            name="replanner",
            model=model,
            description="Proposes and explains alternative shooting schedules after a disruption.",
            instruction=INSTRUCTION,
            tools=[propose_replan, consolidate_schedule],
        )
        self.runner = InMemoryRunner(agent=self.agent, app_name=app_name)
        self.app_name = app_name

    async def replan_async(self, request: str, user_id: str = "producer") -> ReplanResult:
        session = await self.runner.session_service.create_session(
            app_name=self.app_name, user_id=user_id)

        result = ReplanResult(text="")
        message = types.Content(role="user", parts=[types.Part(text=request)])

        async for event in self.runner.run_async(
                user_id=user_id, session_id=session.id, new_message=message):
            for part in (event.content.parts if event.content and event.content.parts else []):
                if getattr(part, "function_call", None):
                    call = part.function_call
                    result.tool_calls.append({"name": call.name, "arguments": dict(call.args or {})})
                if getattr(part, "function_response", None):
                    payload = part.function_response.response or {}
                    options = _extract_options(payload)
                    if options:
                        result.options = options
                if getattr(part, "text", None) and event.is_final_response():
                    result.text = part.text.strip()

        if not result.tool_calls:
            # No solver run means no schedule. Whatever the model wrote is not a plan.
            result.text = (
                "I did not produce a replan: the solver was never asked. "
                "Describe the disruption with a trigger type and a date, e.g. "
                "\"Sherlock Holmes is unavailable for 2 days from 2026-08-11\".")

        return result


def _extract_options(payload: Any) -> List[Dict[str, Any]]:
    """Pull the options out of whatever shape ADK wrapped the tool response in."""
    if isinstance(payload, str):
        try:
            payload = json.loads(payload)
        except ValueError:
            return []
    if isinstance(payload, dict):
        for key in ("options", "result", "response"):
            value = payload.get(key)
            if isinstance(value, list):
                return value
            if isinstance(value, dict) and isinstance(value.get("options"), list):
                return value["options"]
    return []
