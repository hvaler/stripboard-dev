"""
Observing the agent, as distinct from observing the shoot (EV-47).

Everything else in this project points Grafana at the production: days, cost, company moves,
who is being paid to wait. That is the thesis and it stays on the wall. This module is the
other half the Grafana track asks for — *observe the agent you build* — and it deliberately
lands somewhere else: its own metrics, its own dashboard, so that "what is being observed
here?" still has one answer on the page a judge looks at first.

What it records, and why each one:

  `agent.llm.tokens`     What a question costs. "Ask your shoot" runs several Gemini rounds
                         with MCP calls between them, and a question that quietly takes six
                         rounds costs six times one that takes one. Without this the only
                         signal is the bill at the end of the month.
  `agent.llm.duration`   Where the twenty seconds go. Split from the tool time below, because
                         "the model is slow" and "Grafana is slow" need different fixes.
  `agent.mcp.calls`      Every tools/call, labelled by server, tool and outcome. This is the
                         partner integration seen from the inside: not "we use MCP" but which
                         tools, how often, and how many of them fail.
  `agent.mcp.duration`   How long the far end takes.

**Exporting is opt-in and silent when unconfigured.** The agents run on laptops as often as in
Cloud Run, and an SDK that fails loudly because nobody set an endpoint would make a demo look
broken for no reason. With no `OTEL_EXPORTER_OTLP_ENDPOINT` this records to a no-op and costs
nothing — the same standard variables the .NET services already read from Secret Manager, so
there is one place to configure and one place to rotate.
"""

import logging
import os
import time
from contextlib import contextmanager
from typing import Optional

logger = logging.getLogger("AgentTelemetry")

_meter = None
_llm_tokens = None
_llm_duration = None
_mcp_calls = None
_mcp_duration = None
_initialised = False


def _init() -> None:
    """Wire up OTLP once, or fall back to instruments that do nothing."""
    global _meter, _llm_tokens, _llm_duration, _mcp_calls, _mcp_duration, _initialised
    if _initialised:
        return
    _initialised = True

    if not os.getenv("OTEL_EXPORTER_OTLP_ENDPOINT"):
        logger.debug("No OTLP endpoint configured; agent telemetry is inert.")
        return

    try:
        from opentelemetry import metrics
        from opentelemetry.exporter.otlp.proto.http.metric_exporter import OTLPMetricExporter
        from opentelemetry.sdk.metrics import MeterProvider
        from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
        from opentelemetry.sdk.resources import Resource
    except ImportError:
        # Declared in requirements, but an agent that will not start because a metrics
        # package is missing is a worse outcome than an agent nobody can measure.
        logger.info("OpenTelemetry is not installed; agent telemetry is inert.")
        return

    reader = PeriodicExportingMetricReader(
        OTLPMetricExporter(), export_interval_millis=15_000)
    metrics.set_meter_provider(MeterProvider(
        resource=Resource.create({
            "service.name": os.getenv("OTEL_SERVICE_NAME", "stripboard-agents"),
        }),
        metric_readers=[reader]))

    _meter = metrics.get_meter("Stripboard.Agents")
    _llm_tokens = _meter.create_counter(
        "agent.llm.tokens", unit="{token}", description="Tokens a Gemini call consumed.")
    _llm_duration = _meter.create_histogram(
        "agent.llm.duration", unit="ms", description="Wall time of a Gemini call.")
    _mcp_calls = _meter.create_counter(
        "agent.mcp.calls", unit="{call}", description="MCP tools/call invocations.")
    _mcp_duration = _meter.create_histogram(
        "agent.mcp.duration", unit="ms", description="Wall time of an MCP tools/call.")
    logger.info("Agent telemetry exporting to %s", os.getenv("OTEL_EXPORTER_OTLP_ENDPOINT"))


def record_llm(model: str, tokens: int, milliseconds: float, rounds: int = 1) -> None:
    """One Gemini exchange. `rounds` is how many times the model was asked before it answered."""
    _init()
    if _llm_tokens is None:
        return
    attributes = {"model": model, "rounds": rounds}
    _llm_tokens.add(max(0, tokens), attributes)
    _llm_duration.record(milliseconds, attributes)


@contextmanager
def mcp_call(server: str, tool: str):
    """
    Times one `tools/call` and records whether it succeeded.

    A context manager rather than a decorator so the failure path is counted too: a tool that
    fails is the more interesting event, and a counter that only increments on success reports
    a healthy integration right up until nothing works.
    """
    _init()
    started = time.perf_counter()
    status = "ok"
    try:
        yield
    except Exception:
        status = "error"
        raise
    finally:
        if _mcp_calls is not None:
            attributes = {"server": server, "tool": tool, "status": status}
            _mcp_calls.add(1, attributes)
            _mcp_duration.record((time.perf_counter() - started) * 1000, attributes)
