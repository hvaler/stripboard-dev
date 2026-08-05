# ADR-015 — Deploying the Conflict Sentinel and its MCP server

**Status:** Accepted · 2026-08-05 · Implements EV-31

## Context

"Ask your shoot" (EV-29) worked, and only on a laptop. It needs two processes the web app
does not have: the Gemini SDK with Grafana MCP tooling, and the `grafana/mcp-grafana`
server itself. The hosted demo said "not configured for this deployment", which was honest
and useless — the project's strongest differentiator was invisible to anyone clicking the
submitted URL.

## Decision

### One Cloud Run service, two containers

`stripboard-sentinel` runs the agent as the ingress container and Grafana's official MCP
server as a sidecar. Sidecars share a network namespace, so the agent reaches the MCP
server on `localhost:8000`.

That is the security argument, not just a convenience one: **the MCP server is never
exposed to the internet, and the Grafana service-account token never leaves the instance.**
The alternative — two services talking over the network — would have put a token-bearing
Grafana proxy on a public URL.

`run.googleapis.com/container-dependencies` starts the agent after the MCP server, so the
first request does not race the sidecar's boot. Cloud Run requires the depended-on
container to declare a startup probe; without one the deployment is rejected outright,
which is a good error to get.

### The sentinel is private

Every question costs several Gemini rounds. A public endpoint is a bill any stranger can
run up, so there is no `allUsers` binding: only `sa-stripboard-web` holds
`roles/run.invoker`. The Blazor app authenticates with an identity token minted from the
Cloud Run metadata server for the sentinel's own audience, cached for 45 minutes rather
than re-minted per keystroke.

Off Cloud Run there is no metadata server, so `SentinelClient` sends no token — which is
correct for a local sentinel that is not access-controlled either.

The deploy script verifies both halves and says so: an anonymous request must return
401/403, and an authenticated one must return the health payload. A deployment that
accidentally went public would be caught by its own deploy.

### Least privilege

`sa-sentinel` holds exactly `aiplatform.user`, `logging.logWriter`, and read access to the
`grafana-sentinel-token` secret. It cannot touch the database, the schedule, or anything
else the production owns.

## The build-context trap

The repository-root `.dockerignore` excludes `agents/`, because the web image has no use
for the Python layer. That silently broke the sentinel build: `COPY agents/breakdown/` from
a context where `agents/` does not exist fails with a checksum error that names the path
but not the reason.

Weakening the web image's exclusions to fix a second image would have been the wrong trade.
BuildKit reads `<dockerfile>.dockerignore` when present, so
`agents/Dockerfile.sentinel.dockerignore` gives the sentinel an inverted rule — deny
everything, allow the two agent packages — while the web image keeps excluding them. Both
files still exclude `.secrets/` explicitly rather than relying on the other.

## Verification

Against the deployed services:

- Anonymous `GET /api/health` on the sentinel → **HTTP 403**.
- Authenticated `GET /api/health` → `{"status":"ok","grafana_mcp":"mcp-grafana","tools":73,"gemini_configured":true}`
  — the sidecar is talking to Grafana Cloud from inside Cloud Run.
- "Ask your shoot" from the public web URL, in a browser: *"Prof. James Moriarty and
  Sherlock Holmes are tied for the most idle time, at 33% of shooting days"*, with the
  Grafana queries listed beneath it and no console errors.

That run also exercised the error path by accident and passed: the model's first
`query_prometheus` was rejected — `stepSeconds must be provided when queryType is 'range'`
— the failure was handed back to it rather than hidden, and it retried with
`queryType: instant` and answered. The failed call is shown in the UI marked as failed,
because a producer should be able to see the agent's reasoning, not only its conclusion.

## Consequences

- The differentiator is now demonstrable from the submitted URL, which unblocks the video.
- The sentinel scales to zero, so the first question after an idle period pays a cold start
  for two containers. Worth it: unlike the web app it holds no circuit and no state.
- A second service means a second image to keep current. `infra/deploy-sentinel.sh` builds,
  mirrors Grafana's image into Artifact Registry, deploys, authorises and verifies in one
  run; `infra/deploy-web.sh` discovers the sentinel's URL rather than hardcoding it.
- Still outstanding from the original EV-31 scope: a clean-machine Quickstart run and
  `docs/EVIDENCE.md`.
