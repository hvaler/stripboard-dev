# 🎬 Stripboard — Autonomous Line Producer for Film Shoots

> **The LLM formulates, the solver decides, a human approves.**

Stripboard is a multi-agent system that acts as an autonomous line producer for film
production: it breaks down a screenplay into typed scenes and elements, builds an optimal
shooting schedule, continuously watches for disruptions (cast availability, permits,
weather, union rules), and replans in seconds — with ranked options, cost deltas, and a
human producer approving every commit.

Built for the [Agentic Cinema Hackathon](https://agentic-cinema.devpost.com/)
(Google Cloud) — **Grafana partner track**.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Status](https://img.shields.io/badge/status-work%20in%20progress-orange)
![Gemini](https://img.shields.io/badge/Gemini%202.5%20Flash-Vertex%20AI-4285F4)
![Grafana](https://img.shields.io/badge/Grafana-MCP%20client-F46800)
![Tests](https://img.shields.io/badge/tests-50%20xUnit%20%2B%2040%20python-brightgreen)
![License](https://img.shields.io/badge/license-Apache--2.0-green)

## ⚠️ Implementation status

This is an in-progress hackathon entry, and this section is the contract between what the
rest of this README describes and what the code actually does today. **The architecture
below is the target design, not a description of shipped functionality.**

**Working today:**

- **Screenplay breakdown with Gemini 2.5 Flash on Vertex AI** (`agents/breakdown`) from
  **Fountain, Final Draft `.fdx` or PDF** — a scanned script is read by Gemini
  multimodal. Native structured output against a Pydantic schema, a validation-feedback
  retry loop, and an explicitly-labelled fallback. The agent also separates the
  *location* the unit travels to from the *set* within it, which is what makes the
  company-move count real. See [ADR-009](adr/ADR-009-gemini-structured-output-breakdown.md)
  and [ADR-013](adr/ADR-013-screenplay-formats-and-location-vs-set.md).
- **Conflict Sentinel as a live Grafana MCP client** (`agents/sentinel`), speaking the MCP
  Streamable HTTP transport to the official `grafana/mcp-grafana` server: 73 tools
  discovered on Grafana Cloud, disruptions published as real annotations via
  `create_annotation`. See
  [ADR-010](adr/ADR-010-grafana-mcp-sidecar-transport.md).
- **Deterministic shooting-schedule solver on Google OR-Tools CP-SAT**
  (`src/Stripboard.Solver`): day/night units, Day Out of Days cast availability, permit
  windows, disruption blocks, and a day length that includes its meal break and company
  moves. Union turnaround holds **by construction** rather than being checked afterwards —
  see [ADR-012](adr/ADR-012-scheduling-model.md).
- **The UI is driven by the engine** (EV-21): the stripboard, the replan options and their
  cost deltas are all read from persisted schedule versions produced by real solver runs.
  Importing a different screenplay changes what the board shows.
- **Disruption → replan → human approval**, end to end: a disruption becomes scene-date
  constraints, each replan strategy is a separate CP-SAT run, and only the Producer role
  can commit the result.
- Union rules as pure, tested domain code (`src/Stripboard.Domain/Services/UnionRulesService.cs`):
  12-hour turnaround including midnight crossing, meal penalties, night→day transitions.
- Four ASP.NET Core services exposing schedule / people / locations / weather operations.
- Role-scoped call sheets as PDF via QuestPDF.
- Blazor UI with five pages, and a versioned Grafana dashboard definition.
- 50 xUnit tests and 40 Python tests, green (`dotnet test`, `python -m unittest`). The
  Gemini and Grafana integration tests make real calls and fail — not skip — when the
  service is configured but broken.

**Not implemented yet — do not read the sections below as claims that these exist:**

| Gap | Where | Tracked by |
|---|---|---|
| OpenTelemetry packages are declared but never referenced or initialised, so no traces or metrics reach Grafana Cloud | `Directory.Packages.props` | EV-20 |
| Persistence is in-memory. No Cloud SQL, no migrations | every `Program.cs` | EV-22 |
| The four services are REST endpoints under an `/mcp/` path — they do **not** speak the MCP protocol | `src/Stripboard.Mcp.*` | EV-23 |
| The replanner returns two hardcoded proposals with literal cost figures | `agents/replanner/replanner_agent.py` | EV-24 |
| No ADK, no Vertex AI Agent Engine, no A2A orchestration | `agents/` | EV-24 → EV-26 |

**Live demo: <https://stripboard-web-wc7oib7k6q-ew.a.run.app>** — deployed on Cloud Run and
verified end to end in a browser with zero console errors: inject a disruption, compare the
costed options, approve as Producer, read the audit trail. See
[ADR-011](adr/ADR-011-blazor-server-on-cloud-run.md) for what Blazor Server needs from Cloud
Run. There is no demo video yet.

## The problem

A shooting schedule is a brutal constraint-satisfaction problem: scenes must be grouped
by location to minimize company moves, cast availability (Day Out of Days) must be
honored, union rules enforced (12-hour turnaround, meal penalties, night-to-day
transitions), permit windows and daylight hours respected. It breaks constantly — an
actor gets sick, weather turns, a permit falls through — and the 1st Assistant Director
replans the whole thing by hand, often overnight, then redistributes call sheets to the
entire crew.

## Target architecture

> Components marked `[✓]` exist today; `[ ]` are designed but not built. See the status
> section above.

```
                     ┌─────────────────────────────┐
                     │   Orchestrator (A2A)     [ ]│
                     │   Vertex AI Agent Engine [ ]│
                     └─────────────┬───────────────┘
        ┌──────────┬───────────────┼───────────────┬────────────┐
   Breakdown   Scheduler      Replanner       Call sheets   Watchers
   (Gemini →   (formulates    (options +      (role-scoped  (availability,
    typed       constraints)   cost deltas)    PDFs)         locations,
    scenes)         │                                         weather)
      [✓]          [✓]             [✓]             [✓]          [ ]
                    ▼                                            │
             CP-SAT solver (Google OR-Tools)              Conflict Sentinel
             deterministic, tested                  [✓]   (read-only, typed
                    │                                      anomalies →
                    ▼                                      Grafana annotations)
        ┌───────────────────────────────────────────┐            [✓]
        │  Service layer: mcp-schedule · mcp-people │
        │  · mcp-locations · mcp-weather            │  REST [✓] / MCP [ ]
        └───────────────────────────────────────────┘
```

Design principles the implementation is being held to:

- **The LLM must never "reason" schedules.** Gemini's role is to extract, formulate and
  explain; a deterministic CP-SAT solver computes; union rules live in the domain layer as
  tested code, not in prompts. The solver and the domain rules already work this way.
- **Nothing commits without a human.** The replanner proposes ranked options with cost
  deltas; only the Producer role can commit a schedule version.
- **Append-only versioning.** Every replan is a new `ScheduleVersion` with its parent,
  author (human or agent) and triggering disruption — the audit trail is free.
- **Least-privilege agents.** Each agent gets its own service account via Workload
  Identity: the sentinel physically cannot write; the replanner cannot commit.
  `infra/iam/setup-agent-iam.sh` creates the accounts; nothing enforces this at runtime yet.

## Technology

| Piece | Technology | Status |
|---|---|---|
| Solver | Google OR-Tools CP-SAT (.NET bindings) | ✅ working |
| Domain & union rules | C# / .NET 10, pure domain layer | ✅ working |
| Call sheets | QuestPDF | ✅ working |
| Web UI | Blazor Server (.NET 10) | ✅ driven by the solver |
| Services | ASP.NET Core (.NET 10) minimal APIs | ✅ REST · ❌ not MCP yet |
| Data | EF Core 9 | 🚧 in-memory only, no Cloud SQL |
| Grafana dashboard | Versioned JSON + provisioning script (`infra/grafana/`) | ✅ working |
| Screenplay breakdown | Gemini 2.5 Flash on Vertex AI (`google-genai`, structured output) | ✅ working |
| Agent orchestration | ADK, Vertex AI Agent Engine, A2A | ❌ not implemented |
| Observability (partner) | OpenTelemetry OTLP → Grafana Cloud | ❌ not wired |
| **Partner integration** | Grafana MCP client — Streamable HTTP, JSON-RPC 2.0, 73 tools | ✅ working |
| Security | Cloud IAM, per-agent service accounts, Secret Manager | 🚧 script only |

### Grafana partner track

The track requires the Grafana stack to be used at runtime through the official
`grafana/mcp-grafana` MCP server. Stripboard does this for real:

- **`agents/sentinel/grafana_mcp_client.py`** implements the MCP **Streamable HTTP**
  transport directly over JSON-RPC 2.0 — `initialize` with session negotiation,
  `notifications/initialized`, paginated `tools/list`, and `tools/call` — accepting both
  JSON and SSE responses. No client library; the only dependency is `requests`.
- **73 tools** are discovered at runtime against Grafana Cloud, including
  `create_annotation`, `get_annotations`, `alerting_manage_rules`, `query_prometheus`,
  `query_loki_logs` and `search_dashboards`.
- **Disruptions are published as Grafana annotations through the MCP server**, not through
  the REST API, and are read back attributed to the sentinel's service account.
- The server runs as a **sidecar we control** (`infra/grafana/run-mcp-sidecar.sh`). The
  hosted Grafana Cloud MCP endpoint authorises via interactive OAuth 2.1, which an
  unattended agent cannot complete — see [ADR-010](adr/ADR-010-grafana-mcp-sidecar-transport.md).
- The **"Shoot Mission Control"** dashboard is versioned JSON provisioned by script.

Still pending: OTLP traces and metrics streaming to Grafana Cloud (EV-20), and the
reasoning layer that queries metrics over MCP and lets Gemini interpret them (EV-29).

## Repository layout

```
src/        .NET solution: Domain, Application, Infrastructure, Solver,
            Mcp.* services, CallSheets, Web (Blazor)
agents/     Python agent layer (breakdown, sentinel, replanner) — see status above
tests/      xUnit: domain rules, solver, service contracts, call sheets
infra/      Grafana dashboard + provisioning, per-agent IAM setup
adr/        Architecture Decision Records (ADR-005, ADR-008 … ADR-013)
demo/       Sample screenplay, demo harness, submission notes
```

## Quickstart

> Verified locally on Windows with the .NET 10 SDK and Python 3.12. The full cloud
> deployment path (Cloud Run, Cloud SQL, Agent Engine, Grafana provisioning) is not
> scripted yet — that is EV-31.

```bash
git clone https://github.com/hvaler/stripboard-dev.git && cd stripboard-dev

# Build and run the .NET test suite (50 tests)
dotnet test Stripboard.slnx

# Run the web UI at http://localhost:5164 — it seeds a screenplay and solves a
# schedule on first start, so the stripboard has real data immediately.
dotnet run --project src/Stripboard.Web

# Run a service, e.g. the schedule API
dotnet run --project src/Stripboard.Mcp.Schedule

# Run the local demo harness (stubbed pipeline, no cloud dependencies)
python demo/run_demo.py
```

### Screenplay breakdown with Gemini

Requires Google Cloud credentials. Vertex AI is the default backend:

```bash
pip install -r agents/breakdown/requirements.txt

gcloud auth application-default login
export GOOGLE_CLOUD_PROJECT=<your-gcp-project>   # needs aiplatform.googleapis.com enabled

# Real extraction — `-v` shows the Vertex AI call and the token count
python -m agents.breakdown --file demo/screenplay.fountain -v

# A screenplay unrelated to the demo script, to show it generalises
python -m agents.breakdown --file demo/screenplay-harbour.fountain

# Final Draft and PDF are read too — the PDF goes through Gemini multimodal
python -m agents.breakdown --file demo/screenplay-metropole.fdx
python -m agents.breakdown --file demo/screenplay-metropole.pdf

# Replay a cached breakdown without calling the model
python -m agents.breakdown --file demo/screenplay.fountain --offline
```

Feed a breakdown straight into the stripboard — the board re-solves and the screen changes:

```bash
python -m agents.breakdown --file demo/screenplay-harbour.fountain --json \
  | curl -s -X POST http://localhost:5164/api/breakdown/import \
         -H 'Content-Type: application/json' --data-binary @-
```

Alternatively set `GEMINI_API_KEY` to use the Gemini Developer API instead of Vertex AI.

Python tests (the breakdown integration tests make real Gemini calls, and skip when no
credentials are configured):

```bash
python -m unittest discover -s agents/breakdown -p "test_*.py"
python -m unittest discover -s agents/sentinel  -p "test_*.py"
```

### Grafana MCP integration

Needs Docker and a Grafana **service account** token (`glsa_` prefix — a Cloud Access
Policy `glc_` token is rejected with 401):

```bash
export GRAFANA_URL=https://<your-stack>.grafana.net
export GRAFANA_SERVICE_ACCOUNT_TOKEN=glsa_xxx

# Start the official grafana/mcp-grafana server as a sidecar
./infra/grafana/run-mcp-sidecar.sh
export GRAFANA_MCP_ENDPOINT=http://localhost:8000/mcp

# Publish real disruption annotations through the MCP server
python demo/run_demo.py

# Integration tests: handshake, tools/list, tools/call, annotation round-trip
python -m unittest discover -s agents/sentinel -p "test_*.py"

# Provision the Shoot Mission Control dashboard
python infra/grafana/provision-dashboard.py
```

No Grafana Cloud stack? The same flow works against a local Grafana:

```bash
docker run -d --name grafana -p 3000:3000 grafana/grafana:latest
# then create a service account token in Administration → Users and access
export GRAFANA_URL=http://host.docker.internal:3000
```

### Deploying

```bash
# Deploys to Cloud Run with the settings a Blazor Server circuit needs (ADR-011)
# and refuses to run without a .dockerignore, so .secrets/ can never enter an image.
bash infra/deploy-web.sh

# Readiness — note /api/health, not /healthz: Google intercepts that path on run.app
curl https://<your-service>.run.app/api/health
```

Optional, and requiring credentials you must supply yourself:

```bash
# Create the per-agent service accounts (needs gcloud + a GCP project)
bash infra/iam/setup-agent-iam.sh
```

## The 3-minute demo

Not recorded yet. A shot-by-shot script lives in
[`demo/submission-checklist.md`](demo/submission-checklist.md); it can only be narrated
once the corresponding features are real.

## Development notes

This project is developed primarily with AI assistance (Google Antigravity) under an
internal engineering standard (Clean Architecture/DDD, tested domain rules,
conventional commits, ADRs). The product is designed so that all runtime AI is Gemini on
Google Cloud; no third-party AI SDK is present in this repository.

## License

[Apache-2.0](LICENSE)
