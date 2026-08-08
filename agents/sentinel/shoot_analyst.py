"""
"Ask your shoot" — Gemini reasoning over the live Grafana stack via MCP (EV-29).

The loop is the point. Tools are not hardcoded: they are discovered from the Grafana MCP
server at runtime with `tools/list`, converted into Gemini function declarations, and
executed back through MCP when the model asks for them. Adding a tool to Grafana adds a
capability here with no code change.

What this deliberately does not do is let the model invent numbers. Every figure in an
answer comes from a tool result; the model's job is to choose the query and read the
result back in English.
"""

import json
import logging
import time
import os
import sys
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

from google.genai import types

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "common")))

from grafana_mcp_client import GrafanaMcpClient, GrafanaMcpError  # noqa: E402
# One schema adapter for every MCP server we expose to Gemini. It used to live here, which
# was fine until a second caller needed it.
from mcp_client import sanitise_schema as _sanitise_schema  # noqa: E402,F401
import telemetry  # noqa: E402

logger = logging.getLogger("ShootAnalyst")

MAX_TOOL_ROUNDS = 6

# Read-only tools worth exposing for questions about a shoot. The server offers 70+, but a
# focused set keeps the model on the signals that matter and the prompt affordable. Nothing
# here can modify the Grafana stack.
TOOL_ALLOWLIST = (
    "query_prometheus",
    "list_prometheus_metric_names",
    "list_prometheus_label_names",
    "list_prometheus_label_values",
    "query_loki_logs",
    "list_datasources",
    "get_annotations",
    "get_annotation_tags",
    "search_dashboards",
    "get_dashboard_summary",
    "alerting_manage_rules",
)

#: The Prometheus datasource these metrics live in.
#:
#: Told to the model rather than discovered, because discovery was an intermittent failure and
#: an intermittent failure is worse than a broken one: it rehearses clean and breaks on camera.
#: The model would call `list_datasources`, occasionally omit its arguments, and then issue
#: `query_prometheus` without a usable uid — which Grafana answers with a permissions error, so
#: the producer is told the risk index is unavailable when what actually happened is that the
#: agent forgot which database it was talking to. Naming it here also removes a whole round
#: trip from every answer. The dashboards pin the same uid; this is stack configuration, not a
#: shortcut, and it is overridable for a stack where it differs.
PROM_DATASOURCE_UID = os.getenv("GRAFANA_PROM_DATASOURCE_UID", "grafanacloud-prom")

SYSTEM_INSTRUCTION = f"""You answer questions about a film shoot by querying Grafana.

The Prometheus datasource uid is `{PROM_DATASOURCE_UID}`. Pass it as `datasourceUid` on every
query; you do not need to call `list_datasources` to find it. `query_prometheus` also requires
`endTime` — use `"now"` — and `queryType: "instant"` for a current value.

The production exports these metrics, all prefixed `shoot_`:
- shoot_days_total — shooting days in the committed schedule
- shoot_company_moves — times the unit changes location; each costs an hour of the day
- shoot_cost_estimate_usd — estimated cost of the schedule
- shoot_union_violations — union rule violations found in the schedule
- shoot_risk_index — 0-100 heuristic of schedule fragility, not a probability
- shoot_locations_per_day_max — locations visited on the worst day; more than two means a
  day largely spent moving the unit rather than shooting
- shoot_cast_utilization — per `actor`, the fraction of shooting days they are called for
- shoot_scenes_total, shoot_eighths_total — size of the screenplay scheduled
- solver_solve_duration_ms, solver_solves_total — the CP-SAT solver itself

**That list is complete.** If the metric you want is not on it, it does not exist — do not
query a plausible-sounding name. Asked what the production has "burned", "spent" or "is
projected to overrun", the metric is `shoot_cost_estimate_usd`; there is no burn or projection
series, and querying `shoot_cost_burn_usd` returns nothing, which reads to a producer as a
budget that cannot be found rather than as a question this system does not answer.

A metric with no series means no schedule has been committed yet. Say that; it is not zero.

Disruptions are published as Grafana annotations tagged `stripboard` and `conflict-sentinel`.

How to answer:
- Query before you answer. Never state a number you have not read from a tool result.
- If a query returns nothing, say so plainly rather than guessing what the value might be.
- Answer as a line producer would: short, concrete, and about the shoot rather than about
  the monitoring system. Name the figure and what it means for the schedule.
- Asked who or which, name them. "Every actor is used to some extent" is a true sentence that
  answers nothing; the producer wants the names and their figures, lowest first. An actor
  called on half a four-day shoot is two days of a contract paid against no work.
- **Ratios are said in words, never as decimals.** `shoot_cast_utilization` comes back as a
  fraction between 0 and 1: 0.25 is "a quarter of the shooting days", 0.5 is "half", 0.75 is
  "three quarters", 1 is "every shooting day". Write the words. For a figure with no tidy
  fraction, round to a percentage — 0.6 is "about sixty per cent of the days". A line producer
  reads this off a phone between setups and does not convert decimals in their head; "Figure is
  utilised for 0.25 of the shooting days" is monitoring-speak, and this answer goes to a person
  deciding whether to release an actor.
- When a metric name is uncertain, list metric names first instead of guessing. Confidence is
  not evidence: the failure this rule exists to prevent was a query for a metric that had
  never existed, issued without hesitation because the name sounded right.
- Write plain prose, with no Markdown. The page renders your answer as text, so asterisks
  and bullet characters arrive on screen exactly as you typed them. When you have several
  figures to give, put them in a sentence — "Clerk, Dr. Priya Nair and Figure are each called
  on a quarter of the shooting days" reads better on a call sheet than a bulleted list
  anyway."""


@dataclass
class ToolCall:
    name: str
    arguments: Dict[str, Any]
    result: Any = None
    error: Optional[str] = None


@dataclass
class Answer:
    text: str
    tool_calls: List[ToolCall] = field(default_factory=list)
    total_tokens: int = 0
    rounds: int = 0


class ShootAnalyst:
    """Answers natural-language questions about the shoot using the Grafana MCP server."""

    def __init__(self, gemini_client, grafana: GrafanaMcpClient, allowlist=TOOL_ALLOWLIST):
        self.gemini = gemini_client
        self.grafana = grafana
        self.allowlist = set(allowlist) if allowlist else None

    def available_tools(self) -> List[Dict[str, Any]]:
        tools = self.grafana.list_tools()
        if self.allowlist is None:
            return tools
        return [t for t in tools if t["name"] in self.allowlist]

    def _declarations(self) -> List[types.Tool]:
        declarations = []
        for tool in self.available_tools():
            declarations.append(types.FunctionDeclaration(
                name=tool["name"],
                description=(tool.get("description") or tool["name"])[:1000],
                parameters=_sanitise_schema(tool.get("inputSchema") or {"type": "object", "properties": {}}),
            ))
        logger.info("Exposing %d Grafana MCP tools to Gemini", len(declarations))
        return [types.Tool(function_declarations=declarations)]

    def ask(self, question: str) -> Answer:
        """Times and counts the exchange, then delegates. See agents/common/telemetry.py."""
        started = time.perf_counter()
        answer = self._ask(question)
        telemetry.record_llm(
            model=self.gemini.model,
            tokens=answer.total_tokens,
            milliseconds=(time.perf_counter() - started) * 1000,
            rounds=answer.rounds)
        return answer

    def _ask(self, question: str) -> Answer:
        client = self.gemini._ensure_client()  # noqa: SLF001 - same package, one owner
        tools = self._declarations()

        def config_for(round_number: int) -> types.GenerateContentConfig:
            # The first turn MUST query Grafana. Asked politely in the prompt, the model
            # will sometimes answer straight from its own head — during development it
            # confidently reported a risk index of 75 when the real value was 54. Forcing
            # a function call makes "never state a number you have not read" a property of
            # the request rather than a request the model may decline.
            mode = (types.FunctionCallingConfigMode.ANY if round_number == 1
                    else types.FunctionCallingConfigMode.AUTO)
            return types.GenerateContentConfig(
                tools=tools,
                system_instruction=SYSTEM_INSTRUCTION,
                temperature=0.1,
                tool_config=types.ToolConfig(
                    function_calling_config=types.FunctionCallingConfig(mode=mode)),
                # The tools are MCP calls, so this loop dispatches them rather than the SDK.
                automatic_function_calling=types.AutomaticFunctionCallingConfig(disable=True),
            )

        contents: List[types.Content] = [
            types.Content(role="user", parts=[types.Part(text=question)])
        ]
        answer = Answer(text="")

        for round_number in range(1, MAX_TOOL_ROUNDS + 1):
            response = client.models.generate_content(
                model=self.gemini.model, contents=contents, config=config_for(round_number))

            usage = response.usage_metadata
            answer.total_tokens += getattr(usage, "total_token_count", 0) or 0
            answer.rounds = round_number

            candidate = response.candidates[0] if response.candidates else None
            parts = list(candidate.content.parts) if candidate and candidate.content else []
            calls = [p.function_call for p in parts if getattr(p, "function_call", None)]

            if not calls:
                text = "".join(p.text for p in parts if getattr(p, "text", None)).strip()

                if not answer.tool_calls:
                    # Nothing was ever read from Grafana, so whatever the model wrote is
                    # not evidence. Better to say so than to publish a plausible number.
                    finish = getattr(candidate, "finish_reason", None)
                    answer.text = (
                        "I could not answer that from the shoot's telemetry — no Grafana "
                        f"query was made (model finish reason: {finish}). "
                        "Try naming the metric you care about, e.g. company moves, cost or risk index."
                    )
                    logger.warning("Answer refused: model returned no tool call. finish=%s", finish)
                    return answer

                answer.text = text or "The Grafana queries returned no data for that question."
                return answer

            contents.append(types.Content(role="model", parts=parts))

            response_parts = []
            for call in calls:
                arguments = dict(call.args or {})
                record = ToolCall(name=call.name, arguments=arguments)
                try:
                    record.result = self.grafana.call_tool(call.name, arguments)
                    logger.info("Grafana MCP tool %s -> ok", call.name)
                except GrafanaMcpError as exc:
                    # Hand the failure back to the model: "that query was rejected" is
                    # information it can act on, and hiding it would invite invention.
                    record.error = str(exc)
                    logger.warning("Grafana MCP tool %s failed: %s", call.name, exc)

                answer.tool_calls.append(record)
                payload = {"error": record.error} if record.error else {"result": record.result}
                response_parts.append(types.Part.from_function_response(
                    name=call.name, response=_jsonable(payload)))

            contents.append(types.Content(role="user", parts=response_parts))

        answer.text = (
            f"Stopped after {MAX_TOOL_ROUNDS} rounds of Grafana queries without reaching an "
            "answer. Try a narrower question."
        )
        return answer


def _jsonable(value: Any) -> Dict[str, Any]:
    """Function responses must be JSON-serialisable; Grafana returns arbitrary shapes."""
    try:
        json.dumps(value)
        return value if isinstance(value, dict) else {"value": value}
    except (TypeError, ValueError):
        return {"value": str(value)}
