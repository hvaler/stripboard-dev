# ADR-011 — Running Blazor Server on Cloud Run

**Status:** Accepted · 2026-08-04 · Implements EV-30

## Context

The first deployment of the web app resolved but was unusable: it rendered the shell and
then immediately showed Blazor's circuit-failure UI — *"An unhandled error has occurred /
Rejoining the server… / The session has been paused by the server"*. A judge clicking the
submitted URL would have formed their entire Design impression from that screen.

Blazor Server keeps a **stateful SignalR circuit** per user: UI state lives on the server
and every interaction is a round trip. Cloud Run's defaults are all wrong for that shape of
application, and each default fails in a way that looks like a different bug.

## Decision

Keep Blazor Server and configure the platform for it, rather than rewriting the UI as
static SSR. The interactivity is the product — injecting a disruption and comparing costed
options is the demo. The deployment settings live in `infra/deploy-web.sh`:

| Setting | Default | Why the default breaks this app |
|---|---|---|
| `--no-cpu-throttling` | CPU throttled between requests | **The most important one.** A circuit is idle between clicks, so SignalR's keep-alive never runs and the connection is torn down. This alone produces the "Rejoining the server…" screen. |
| `--session-affinity` | off | A reconnect can land on an instance that has never heard of the caller's circuit. |
| `--min-instances 1` | 0 | Scale-to-zero destroys every live circuit and puts a cold start in front of the visitor. |
| `--max-instances 1` | 20 | Persistence is still in-memory (EV-22). A second instance serves a different schedule, so a disruption injected on one is invisible on the other. |
| `--timeout 3600` | 300s | The WebSocket is a single long-lived request; 300s severs it mid-session. |

Two application-side changes were needed as well:

- **`UseForwardedHeaders`**: Cloud Run terminates TLS and forwards plain HTTP. Without it
  the app considers every request insecure and `UseHttpsRedirection` bounces it, which
  breaks the WebSocket upgrade.
- **Circuit retention**: `DisconnectedCircuitRetentionPeriod` of 5 minutes so a brief
  network blip reconnects instead of dropping the user.

`--max-instances 1` is a deliberate, temporary constraint, not a scaling strategy. It is the
honest consequence of in-memory persistence and should be lifted the moment EV-22 lands.

## Two traps worth recording

**`/healthz` is not yours to use.** Google's frontend intercepts that exact path on
`*.run.app` domains and answers 404 itself; the request never reaches the container. It
worked perfectly in Docker locally, which makes it a genuinely confusing thing to debug.
`/healthz/` with a trailing slash, `/health`, and `/livez` all pass through untouched. The
probe is therefore `/api/health`.

**`COPY . .` without a `.dockerignore`.** The build context was 1.8 GB and included
`.secrets/`, so a deploy would have baked a live Grafana service-account token into an image
layer. Both `.dockerignore` (what enters an image) and `.gcloudignore` (what leaves this
machine for Cloud Build) now exclude it explicitly; `deploy-web.sh` refuses to run if
`.dockerignore` is missing. Relying on gcloud's implicit fallback to `.gitignore` would have
been an accident waiting to happen.

## Verification

Browser walkthrough against the public URL, driven end to end:

- Stripboard renders the committed schedule; **zero console errors or warnings**.
- Injecting a weather disruption runs two CP-SAT solves server-side and returns two options
  with different cost deltas (+$700 vs +$1,800) — proving the circuit survives a
  multi-second server operation.
- Committing as `sa-replanner` is refused with *"Only the Producer role may commit — agents
  propose, humans decide"*; committing as Producer succeeds and writes to the audit trail.
- The audit trail shows the full chain: seed → commit → disruption → two drafts → two
  proposals → commit.
- `/api/health` returned 200 on 8 consecutive requests.

## Consequences

- The hosted URL is demonstrable, which unblocks the video (EV-32).
- The service now runs one always-on instance, so it costs money continuously rather than
  only when visited. That is the right trade for a judged submission with a fixed deadline.
- In-memory state resets on every deploy, so a demo recorded against a specific schedule
  must be recorded after the final deploy. EV-22 removes this.
