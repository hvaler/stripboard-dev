# ADR-014 — Observing the shoot, not the software

**Status:** Accepted · 2026-08-05 · Implements EV-29, and the OTLP export EV-20 was holding

## Context

Every entry in the Grafana track can point Grafana at its own request latency. That is the
obvious use, and it says nothing about the product.

A film shoot is already a real-time system: it has a budget burning down, a risk profile
that moves when an actor falls ill, and cast on payroll whether or not they are called that
day. Those are the numbers a 1st AD tapes to a wall. Exporting *those* through OpenTelemetry
turns Grafana from a tool watching our web app into the control room of the production.

## Decision

### The metrics are about the production

`ShootMetrics` exports, from the committed schedule:

| Metric | What it tells a producer |
|---|---|
| `shoot_days_total` | Length of the shoot |
| `shoot_company_moves` | Times the unit moves; each costs an hour of the day |
| `shoot_cost_estimate_usd` | Crew + called cast per day, plus move and penalty costs |
| `shoot_union_violations` | Above zero means a schedule that cannot legally be shot |
| `shoot_risk_index` | Heuristic 0-100 fragility index — explicitly **not** a probability |
| `shoot_cast_utilization{actor}` | Who is being paid to wait |
| `solver_solve_duration_milliseconds`, `solver_solves_total` | The engine itself |

Application traces and HTTP metrics are exported too, but they are the supporting cast.

### The exporter is configured, not coded

The OpenTelemetry SDK reads `OTEL_EXPORTER_OTLP_*`, so `infra/deploy-web.sh` mounts the
Grafana Cloud credential straight from Secret Manager into the environment. No code reads a
secret; the repository contains no endpoint credential.

### "Ask your shoot": tools discovered, not hardcoded

`ShootAnalyst` answers natural-language questions by listing the Grafana MCP server's tools
at runtime, converting them into Gemini function declarations, and executing the model's
chosen calls back through MCP. Adding a tool to Grafana adds a capability with no code
change. Asked *"which actor am I paying for the most idle time?"*, Gemini composed
`max by (actor) (1 - shoot_cast_utilization)` on its own — and the UI shows that query
underneath the answer, so a producer can see where the number came from.

## The failure that shaped this

Early on, the analyst answered *"the schedule risk index is 75"* without querying anything.
The real value was 54. The system prompt already said **never state a number you have not
read from a tool result**; the model simply declined.

The fix was not a firmer prompt. The first turn now forces `FunctionCallingConfigMode.ANY`,
so a query is a property of the request rather than a request the model may ignore. And if a
run somehow ends with no tool call at all, the answer is discarded and replaced with a
refusal — a plausible fabricated figure is worse than no answer, especially on a page whose
entire purpose is to be trusted.

That is the same principle the project applies to the solver, arriving at the LLM layer:
**the model formulates, the data decides.**

## Two smaller traps

**Metric names carry their units.** The OpenTelemetry-to-Prometheus mangler appends the unit
to the name, so `shoot.cost_estimate_usd` with unit `USD` became
`shoot_cost_estimate_usd_USD`, and `shoot.risk_index` became `shoot_risk_index_ratio`. Every
dashboard query and the analyst's prompt name these metrics, so units are now written in
braces (`{usd}`, `{index}`), which the mangler drops.

**Startup work is invisible.** The initial solve ran before `app.Run()`, therefore before
the OpenTelemetry pipeline existed, so the solve that produces the schedule everyone sees
never appeared in `solver_*`. It now runs on `ApplicationStarted`; `/api/health` reports
degraded until it finishes, which is what the deploy script already waits on.

## Verification

Against the project's own Grafana Cloud stack, queried through the MCP server:

```
shoot_days_total          3
shoot_company_moves       8
shoot_cost_estimate_usd   40100
shoot_risk_index          54
shoot_union_violations    0
shoot_cast_utilization    Watson 1.0 · Irene 1.0 · Moriarty 0.67
solver_solves_total       1
```

Every panel on the Shoot Mission Control dashboard now has a query behind it — before this
they were placeholders with empty targets. "Ask your shoot" answers from the browser, and
its answers match those numbers.

## Consequences

- **EV-20 is delivered by this work**: OTLP traces and metrics reach Grafana Cloud.
- The analyst runs as a small Python service (`agents/sentinel/service.py`) because the
  Gemini SDK and MCP client live there. The Blazor page calls it over HTTP and says so
  plainly when it is not configured. Deploying that service alongside the MCP sidecar is
  outstanding — until then "Ask your shoot" is a local capability, not a hosted one.
- Answering a question costs 3-4 Gemini rounds and roughly 11-17k tokens.
- The risk index is a heuristic. It is labelled as one in the metric description, on the
  dashboard panel and in the prompt, because a number between 0 and 100 invites being read
  as a probability.
