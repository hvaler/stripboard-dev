# ADR-023 — The agents consume our own MCP servers

**Status:** Accepted · 2026-08-06 · Completes EV-23 · Finishes what [ADR-021](ADR-021-our-own-mcp-servers.md) started

## Context

[ADR-021](ADR-021-our-own-mcp-servers.md) made the four `Stripboard.Mcp.*` services speak the
real protocol, and closed with this consequence:

> Stripboard is now an MCP **server** as well as a client. An ADK agent **can** consume our
> schedule tools with `MCPToolset` exactly as it consumes Grafana's.

The README turned *can* into *does*:

> An ADK agent consumes them with `MCPToolset` exactly as it consumes Grafana's.

That sentence was false. `grep -rn "MCPToolset" agents/` returned nothing, no `.csproj`
referenced `Stripboard.Mcp.*` outside its own test project, and every agent reached the
engine over the web app's REST API — `POST /api/replan`, `/api/schedule/commit`. The four
servers were a second interface with 33 contract tests and no consumer.

Two things were wrong with that, and only one of them was the sentence. A parallel interface
nobody calls is the same condition ADR-021 was written to fix: the REST server it replaced
had drifted into committing without an authorisation check *because nothing exercised it*.

## Decision

**The scheduler and governance specialists get their tools from `mcp-schedule` at runtime.**
Not adapters written in Python that happen to speak MCP underneath — the tools are read from
the server with `tools/list`, their MCP input schemas become Gemini function declarations,
and invoking one is a `tools/call`. Adding a tool to `ScheduleTools.cs` gives the agents a new
capability with no Python change.

### Not ADK's `MCPToolset`

ADK ships one, and we do not use it. `google.adk.tools.mcp_tool.mcp_toolset` opens with
`from mcp import SamplingCapability` — the reference Python SDK, published by a vendor the
hackathon rules name explicitly. [ADR-010 §2](ADR-010-grafana-mcp-sidecar-transport.md) already
refused that dependency for the Grafana client and hand-wrote the transport instead. Taking it
here would have contradicted that decision to save about eighty lines.

So `agents/orchestrator/mcp_tools.py` implements ADK's own extension point — `BaseTool` with
`_get_declaration()` and `run_async()` — over the transport we already own.

### One transport, not two

`GrafanaMcpClient` was already a complete Streamable HTTP client. Rather than write a second,
the transport moved to `agents/common/mcp_client.py` and `GrafanaMcpClient` became a
four-line subclass supplying an endpoint, a name and a token. Two copies of a protocol client
is how the two drift apart.

### The role split is the governance model

```python
SCHEDULER_TOOLS  = ("get_schedule", "validate_rules")
GOVERNANCE_TOOLS = ("commit_schedule",)
```

The scheduler cannot attempt a commit. Governance can, and is refused by the server. Naming a
tool the server does not offer raises rather than silently yielding a smaller toolset — a
rename in C# must not quietly remove an agent's ability to do its job, because the first
symptom would be a confident answer that skipped a step.

### REST stays until the servers are deployed

`build_orchestrator(toolset=None)` still wires the REST functions. The MCP servers are not on
Cloud Run yet, so a cloud-only environment needs a working path. The toolset is passed
explicitly and never read from the environment inside the builder: a build whose shape changes
with an env var is a build whose tests describe a different program.

## Two bugs this surfaced, both of the house type

**A stateless server looked disconnected.** Grafana's MCP server is stateful and returns
`Mcp-Session-Id` from `initialize`. Ours run with `options.Stateless = true`, so they return
no such header — deliberately, so Cloud Run can move a request between instances. The client
tracked connection state *as* the session id, so against our own servers it completed the
handshake and then refused to send anything: `Not connected`. Connection state and session id
are now separate.

**A nullable C# parameter produced an invalid Gemini declaration.** `Guid? versionId` is
emitted by the .NET SDK as `"type": ["string", "null"]`. That is ordinary JSON Schema and
Gemini has no union type, so it rejects **the whole declaration**, not the one field. The
scheduler would have started with no tools at all — and an agent with no tools still answers.
The schema adapter now collapses the union to a concrete type plus `nullable: true`.

Neither would have failed loudly. Both are the shape this project keeps finding: not an
absent answer, a wrong one.

## Consequences

- The claim in the README is now true, and narrower than it was: two specialists, over
  `mcp-schedule`. The replanner still reaches `/api/replan` over REST, because the MCP server
  has no replan-from-disruption tool. That is stated rather than glossed.
- `mcp-schedule` seeds and solves an initial schedule at startup, exactly as the web app does
  and behind the same guard. Without it a client completed the handshake, discovered five
  tools, called one and was told there was nothing to schedule — a working protocol against an
  empty database reads as a broken integration.
- 9 unit tests against a deliberately stateless fake server, plus 3 that run against a live
  `mcp-schedule` and **fail rather than skip** when the endpoint is configured and broken.
- `agents/common/` is now on the Conflict Sentinel's image. `Dockerfile.sentinel` and its
  `.dockerignore` were updated in the same change; without that the image builds cleanly and
  dies on its first import.
