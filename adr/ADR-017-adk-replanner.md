# ADR-017 — The replanner as an ADK agent

**Status:** Accepted · 2026-08-05 · Implements EV-24 · Completes the ADK adoption deferred in [ADR-009](ADR-009-gemini-structured-output-breakdown.md)

## Context

`replanner_agent.py` was the last thing in the repository that made numbers up. It returned
two hardcoded proposals — $1,500 and $8,500 — and called swapping two elements of a list
"planning". Its test asserted those very figures, so the suite passed while the agent
produced fiction.

Meanwhile EV-21 had built a real replanner in .NET: each option is a separate CP-SAT run
under different constraints, and each cost delta is the difference between two solved
schedules. Two replanners existed, one honest and one not.

## Decision

### The agent explains; it does not compute

`POST /api/replan` exposes the solver-backed replanner. The ADK agent has exactly one tool,
`propose_replan`, which calls it. The agent's work is to read a disruption described in
English, decide which trigger type and dates model it, and explain the options that come
back.

This is the project's rule reaching the agent layer — *the LLM formulates, the solver
decides, a human approves* — and it is enforced structurally rather than by instruction.
The agent has no arithmetic available to it. There is nothing to hallucinate with, because
every figure arrives from the tool and the tool cannot be answered without the solver
running.

A regression test asserts the module body contains no schedule figures at all, so the old
literals cannot creep back.

### Why not rebuild the replanner in Python

Because it exists and it is correct. Duplicating solver-driven replanning in the agent layer
would have created a second implementation to keep in step, and the one that drifts is
always the one nobody watches. The Python side becomes a client of the engine.

### ADK earns its place here

The manual function-calling loop written for "Ask your shoot" (ADR-014) was justified there:
tools are discovered from MCP at runtime and dispatched back through it. Here the tool is an
ordinary Python function, and ADK's `LlmAgent` + `InMemoryRunner` provide the loop, the
session and the declaration generation with no ceremony. `google-adk` is one of the four
packages the rules permit, and — checked deliberately — it does **not** pull the MCP SDK
that ADR-010 avoided; that is an optional extra.

### One credential convention for every agent

The breakdown agent's client treats "a GCP project is configured" as "use Vertex AI". ADK
does not infer that and fails asking for an API key. The agent now sets
`GOOGLE_GENAI_USE_VERTEXAI` when a project is present, so one set of credentials works
across the whole agent layer instead of two families disagreeing about what configured means.

## An encoding bug this surfaced

The API returned `Option A â€" absorb…`. The source file was valid UTF-8; the **compiler**
was not reading it as UTF-8. Without a byte-order mark, csc falls back to the machine's ANSI
codepage, so an em-dash in a string literal compiles correctly on one developer's machine
and ships as mojibake from another's — here, a Spanish-locale Windows.

`<CodePage>65001</CodePage>` in `Directory.Build.props` fixes it for every project. This
matters beyond tidiness: the repository is public and a judge may clone and build it, and
the output should not depend on their locale.

## Verification

Given *"Sherlock Holmes has called in sick and is unavailable for 1 day from 2026-08-10.
What are my options?"*, the agent extracted `trigger_type=CastUnavailability`,
`person_name=Sherlock Holmes`, `start_date=2026-08-10`, `duration_days=1`, called the tool,
and explained two options. Every figure it stated — 3 days, $40,100, −$1,500, 8 company
moves, 0 union violations — matches the solver's output exactly.

The demo harness now runs the whole chain: the sentinel detects two disruptions and
publishes them to Grafana over MCP, then the ADK agent asks the solver and reports options
computed by CP-SAT.

## Consequences

- Nothing in the repository invents a schedule figure any more.
- The agent requires the scheduling service to be running; unreachable means an error, not
  a plausible plan.
- `google-adk` joins the dependency set, bringing FastAPI and friends. It is confined to
  `agents/replanner`; the breakdown agent keeps using `google-genai` directly, because
  single-shot structured extraction gains nothing from a runner.
- Still outstanding from the original plan: the orchestrator and A2A (EV-25) and deployment
  to Agent Engine (EV-26). The scheduler and sentinel are not ADK agents either — the
  sentinel's loop is deliberately hand-rolled for the MCP reason above.
