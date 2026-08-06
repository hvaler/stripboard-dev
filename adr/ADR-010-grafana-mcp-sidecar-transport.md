# ADR-010 — Grafana MCP as a sidecar over Streamable HTTP

**Status:** Accepted · 2026-08-04 · Implements EV-19 · Refines [ADR-008](ADR-008-grafana-mcp-qualifying-use.md)

## Context

ADR-008 committed the Conflict Sentinel to being an active client of the Grafana Cloud MCP
Server, and correctly identified this as the pass/fail criterion for the Grafana partner
track. It was written in the future tense and never implemented: `grafana_mcp_client.py`
made no network calls, returned canned dictionaries, defaulted its token to the literal
`"stub_glsa_token"`, and logged `"Posted Grafana Annotation"` for annotations it never
sent.

Two things had to be settled before it could be built.

**Which endpoint.** ADR-008 assumed the hosted Grafana Cloud MCP endpoint at
`https://<stack>.grafana.net/api/mcp`. That endpoint authorises through **interactive
OAuth 2.1**: a browser redirect and a human clicking approve. An unattended agent —
running in Cloud Run, in CI, or in a recorded demo — cannot complete that flow.

**Which token.** DT-009 recorded a 401 without identifying the cause. It is now confirmed:
the instance API accepts Grafana **service account tokens** (`glsa_` prefix) and rejects
Cloud Access Policy tokens (`glc_`), which is what had been configured.

## Decision

### 1. Run the official `grafana/mcp-grafana` server as a sidecar we control

The rules name the official `grafana/mcp-grafana` server, and running it ourselves gives
token-based authentication that works headless. It is started by
`infra/grafana/run-mcp-sidecar.sh` (Docker locally, a Cloud Run sidecar container in
deployment), configured with `GRAFANA_URL` and `GRAFANA_SERVICE_ACCOUNT_TOKEN`, and speaks
the **Streamable HTTP** transport on `/mcp`.

Pointing it at Grafana Cloud versus a self-hosted Grafana is a two-variable change, so the
same code path serves local development and the submitted deployment.

### 2. Implement the MCP transport directly, with no client library

`GrafanaMcpClient` speaks JSON-RPC 2.0 over Streamable HTTP itself: `initialize` →
`Mcp-Session-Id` captured and echoed on every later request → `notifications/initialized`
→ `tools/list` (paginated) → `tools/call`. Responses are accepted as either
`application/json` or an SSE stream, as the spec requires. `DELETE` terminates the session.

Reasons for hand-rolling roughly 200 lines instead of taking a dependency:

- The rules restrict AI tooling to Google Cloud packages. MCP is a vendor-neutral protocol
  and using it is *mandatory* for this track, but the reference SDK originates from a
  vendor the rules name explicitly. Implementing the transport removes the question
  entirely.
- The only runtime dependency is `requests`.
- ~~EV-24 will consume the same server through ADK's `MCPToolset`~~ — superseded. That
  migration never happened, and for the reason stated directly above it: ADK's `MCPToolset`
  opens with `from mcp import SamplingCapability`. The transport written here was instead
  generalised into `agents/common/mcp_client.py` and now serves both the Grafana server and
  our own ([ADR-023](ADR-023-agents-consume-our-own-mcp-servers.md)).

### 3. Annotations go through MCP, not the REST API

Disruptions are published with the `create_annotation` **tool call**, not a direct POST to
`/api/annotations`. The partner requirement is about the MCP server being used at runtime,
and routing writes through it makes that concrete and verifiable: the annotation comes back
from `get_annotations` attributed to the sentinel's service account.

Dashboard provisioning (`provision-dashboard.py`) stays on the HTTP API — it is
infrastructure setup, not agent runtime.

### 4. Detection stays deterministic; only publication is remote

Cast availability, weather on exteriors and permit windows are hard rules, so they remain
plain Python. Grafana is where disruptions are *published and observed*, not where they are
*decided*. EV-29 adds the reasoning layer that queries metrics and alert rules over MCP and
lets Gemini interpret them.

Without a connected client the Sentinel still detects, and returns every disruption with
`published=False` and `annotation_id=None`. It never implies it wrote to Grafana.

## Verification

Verified against **both** a local Grafana 13.1.1 and the project's **Grafana Cloud** stack,
using the official `grafana/mcp-grafana` image in each case:

- `initialize` returns protocol `2025-06-18` and a session id, with `serverInfo.name = mcp-grafana`.
- `tools/list` returns **65 tools** locally and **73 on Grafana Cloud** (Cloud adds proxied
  and cloud-only tools), including `create_annotation`, `get_annotations`,
  `list_datasources`, `alerting_manage_rules`, `search_dashboards` and `query_prometheus`.
- `list_datasources` against Grafana Cloud returns the stack's real datasources
  (`grafanacloud-*`: Loki alert-state-history, Graphite, k6, Infinity, …).
- `create_annotation` writes real annotations; `get_annotations` reads them back attributed
  to the sentinel's service account.
- A tool that fails server-side (`get_dashboard_by_uid` on a missing uid) raises rather
  than returning an empty result.

### The SSE frame-ordering defect

Testing against Grafana Cloud exposed a real bug that the local server had hidden. The MCP
spec allows a server to send requests and notifications on the SSE stream *before* the
response, and Grafana Cloud does exactly that: the first `tools/call` of a session is
preceded by a `notifications/tools/list_changed` frame.

The client originally returned the first `data:` frame it saw, so it parsed the
notification, found no `result`, and returned an empty dict. The failure was silent — tool
calls appeared to succeed and simply produced nothing. Every affected symptom was "the
first tool call of a session returns empty".

`_parse_body` now matches the JSON-RPC response by request `id` and raises if no matching
frame arrives. This is a correctness requirement of the transport, not a defensive extra.
Locked down by unit tests that replay a two-frame SSE body without needing a live server.

Integration tests in `agents/sentinel/test_sentinel.py` cover all of the above. They skip
when no MCP server is configured and fail — never skip — when one is configured but broken.

## Consequences

- The Grafana partner-track requirement is met at runtime, against the project's own
  Grafana Cloud stack, and is demonstrable.
- **DT-009 is closed.** Root cause confirmed: the stored token was a `glc_` Cloud Access
  Policy token, which the instance API rejects with 401. It has been replaced by a `glsa_`
  service account token, stored as version 2 of the `grafana-sentinel-token` secret in
  Secret Manager; version 1 is disabled.
- A second, related trap was found and documented: a service account created with **no
  basic role** authenticates successfully and passes the MCP handshake and `tools/list`,
  then fails only on the first write with `403 annotations:create`. The sentinel's service
  account needs **Editor**.
- `check_grafana_state()` deliberately avoids OnCall-backed tools such as
  `list_alert_groups`, which 404 on stacks without the OnCall plugin.
- The sentinel's service account needs **Editor**, not Viewer: publishing annotations is a
  write. This is a documented narrowing of the "the sentinel physically cannot write"
  principle in the README — it cannot write *schedules*, but it must be able to annotate.
