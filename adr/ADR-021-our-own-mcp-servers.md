# ADR-021 — Our own services speak MCP

**Status:** Accepted · 2026-08-05 · Implements EV-23 · Makes ADR-001's claim true

## Context

Four services — schedule, people, locations, weather — were described throughout this
repository as MCP servers. They were ASP.NET endpoints under a path that began `/mcp/`:

```csharp
app.MapPost("/mcp/tools/get_schedule", async (GetScheduleRequest request, …) => …);
```

No `initialize` handshake, no `tools/list`, no advertised schemas, no transport. **A path
prefix is not a protocol.** No client could have discovered these, and none ever did.

ADR-001 claimed *"the Python/.NET boundary is the MCP boundary"*. That was half true: we were
a client of Grafana's server ([ADR-010](ADR-010-grafana-mcp-sidecar-transport.md)) and not a
server of anything.

## Decision

The official `ModelContextProtocol.AspNetCore` SDK (2.1.0), `[McpServerToolType]` classes,
and `app.MapMcp("/mcp")`. Verified against a running server:

```
POST /mcp  {"method":"initialize",…}
  → {"protocolVersion":"2024-11-05","capabilities":{"logging":{},"tools":{}},
     "serverInfo":{"name":"Stripboard.Mcp.Weather","version":"1.0.0.0"}}

POST /mcp  {"method":"tools/list"}
  → {"tools":[{"name":"get_forecast","description":"…","inputSchema":{"type":"object",…}}, …]}
```

Stateless transport: these tools are request/response over a database and never call back to
the client for sampling, so there is no session worth keeping — and a stateless server
survives Cloud Run moving a request to a different instance.

### Deleting the schedule server's own copy of scheduling

`ScheduleMcpService` is gone. The tools call `ScheduleService` and `ReplanService` — the
engine the web app and the Python agents already use.

That service had drifted, in the specific way code drifts when nothing exercises it:

- **It committed without checking authorisation at all.** Its test asserted that committing
  as `"producer-hugo"` succeeded, so the test blessed the hole rather than catching it.
- **`validate_rules(scheduleId)` ignored its argument** and validated every `ShootDay` in the
  database. It answered a question nobody asked, confidently.
- **Its meal-penalty check passed call-to-wrap**, counting the meal break as work — a bug
  fixed in the main engine months earlier.

Two implementations of scheduling existed and only one was watched. Removing the unwatched
one also let `Stripboard.Web` drop a project reference it only kept for that class, which in
turn removed an `ErrorOnDuplicatePublishOutputFiles` workaround and an ambiguous
`typeof(Program)`.

### Tool schemas an agent can actually fill

`create_schedule` used to accept a whole `SolverInput` — lists of `Scene` and `Person` nested
inside the argument. That is a legal MCP schema and an unusable one: no agent will assemble
the cast of a production correctly, and one that tries will invent it.

Scenes come from the database. The tool takes the handful of choices a producer actually
makes: identity, start date, days available, and a cap on locations per day. A contract test
asserts every property of that schema is a scalar.

### Every tool says what it does not know

`mcp-weather`'s forecast is a deterministic function of the location name and the date. It
always was, and it was labelled — in a comment. Now `source: "synthetic"` is on every
response *and* the word SYNTHETIC is in every tool description, because a model chooses a
tool from its description and may never read the payload's provenance field. A test asserts
both.

`get_location` used to answer `Status: "Available"` for any string at all, inventing a
location on demand. A location exists because scenes happen there; zero scenes means the name
is not in this production, and the refusal now lists the real ones so the caller can recover.

## The locale bug, for the third time

`DateOnly.TryParse` uses the current culture. `10/08/2026` is the 10th of August on a Spanish
machine and the 8th of October on an American one — the same tool argument silently producing
two different shooting days depending on where the server runs. It also accepted formats the
schema never advertised, teaching a model that the documented one is optional.

`IsoDate.Parse` uses `TryParseExact` with `InvariantCulture` and `yyyy-MM-dd`.

This is the third time a locale default has bitten this project: the compiler's ANSI codepage
fallback ([ADR-017](ADR-017-adk-replanner.md)) and `requests` decoding an SSE body as
ISO-8859-1 ([ADR-019](ADR-019-alerting-on-the-shoot.md)) were the same mistake wearing
different clothes. **A default that guesses produces something wrong rather than something
absent**, which is why none of the three failed loudly.

## Consequences

- 33 contract tests now drive the real protocol through an in-memory transport: handshake,
  `tools/list`, `tools/call`, schemas, and MCP's error shape. The tests they replace called
  the service classes directly and proved nothing about the protocol.
- A tool that fails returns an ordinary result with `isError: true` rather than a protocol
  error, so a model can read the reason and try something else. Our tools throw
  `McpException` for that; an escaped exception would be a broken server.
- Stripboard is now an MCP **server** as well as a client. ~~An ADK agent can consume our
  schedule tools with `MCPToolset` exactly as it consumes Grafana's.~~ *Can* was true and
  became *does* in the README, which was not. The agents consume these servers as of
  [ADR-023](ADR-023-agents-consume-our-own-mcp-servers.md) — over our own transport, not
  `MCPToolset`, which imports a dependency [ADR-010 §2](ADR-010-grafana-mcp-sidecar-transport.md)
  refused.
- The four servers are not yet deployed. They run and speak the protocol; putting them on
  Cloud Run is deployment work, not protocol work.
