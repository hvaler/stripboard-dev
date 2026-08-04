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
![Tests](https://img.shields.io/badge/tests-24%20xUnit%20%2B%2014%20python-brightgreen)
![License](https://img.shields.io/badge/license-Apache--2.0-green)

## ⚠️ Implementation status

This is an in-progress hackathon entry, and this section is the contract between what the
rest of this README describes and what the code actually does today. **The architecture
below is the target design, not a description of shipped functionality.**

**Working today:**

- **Screenplay breakdown with Gemini 2.5 Flash on Vertex AI** (`agents/breakdown`), using
  native structured output against a Pydantic schema, with a validation-feedback retry
  loop and an explicitly-labelled fallback. See [ADR-009](adr/ADR-009-gemini-structured-output-breakdown.md).
- Deterministic shooting-schedule solver on Google OR-Tools CP-SAT
  (`src/Stripboard.Solver`), with an exactly-one-day assignment, per-day capacity in
  eighths, and permit-window constraints.
- Union rules as pure, tested domain code (`src/Stripboard.Domain/Services/UnionRulesService.cs`):
  12-hour turnaround including midnight crossing, meal penalties, night→day transitions.
- Four ASP.NET Core services exposing schedule / people / locations / weather operations.
- Role-scoped call sheets as PDF via QuestPDF.
- Blazor UI with five pages, and a versioned Grafana dashboard definition.
- 24 xUnit tests and 4 Python tests, green (`dotnet test`, `python -m unittest`).

**Not implemented yet — do not read the sections below as claims that these exist:**

| Gap | Where | Tracked by |
|---|---|---|
| **The Grafana Cloud MCP client is a stub** and performs no network I/O — this is the one remaining pass/fail gap | `agents/sentinel/grafana_mcp_client.py` | EV-19 |
| OpenTelemetry packages are declared but never referenced or initialised | `Directory.Packages.props` | EV-20 |
| The Grafana Annotations API is never actually called | `agents/sentinel/grafana_mcp_client.py` | EV-20 |
| The Blazor UI renders hardcoded data; it does not call the solver or the agents | `src/Stripboard.Web/Pages/` | EV-21 |
| Persistence is in-memory. No Cloud SQL, no migrations | every `Program.cs` | EV-22 |
| The four services are REST endpoints under an `/mcp/` path — they do **not** speak the MCP protocol | `src/Stripboard.Mcp.*` | EV-23 |
| The replanner returns two hardcoded proposals with literal cost figures | `agents/replanner/replanner_agent.py` | EV-24 |
| No ADK, no Vertex AI Agent Engine, no A2A orchestration | `agents/` | EV-24 → EV-26 |
| The solver models neither cast availability (DOOD) nor company moves; turnaround is validated after solving rather than constrained during it | `src/Stripboard.Solver` | EV-27 |

A deployment exists at `https://stripboard-web-wc7oib7k6q-ew.a.run.app` but is **currently
unstable**: Blazor Server's SignalR circuit drops on Cloud Run without session affinity
(EV-30). There is no demo video yet.

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
      [✓]          [ ]             [ ]             [✓]          [ ]
                    ▼                                            │
             CP-SAT solver (Google OR-Tools)              Conflict Sentinel
             deterministic, tested                  [✓]   (read-only, typed
                    │                                      anomalies →
                    ▼                                      Grafana annotations)
        ┌───────────────────────────────────────────┐            [ ]
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
| Web UI | Blazor Server (.NET 10) | 🚧 pages render hardcoded data |
| Services | ASP.NET Core (.NET 10) minimal APIs | ✅ REST · ❌ not MCP yet |
| Data | EF Core 9 | 🚧 in-memory only, no Cloud SQL |
| Grafana dashboard | Versioned JSON + provisioning script (`infra/grafana/`) | ✅ working |
| Screenplay breakdown | Gemini 2.5 Flash on Vertex AI (`google-genai`, structured output) | ✅ working |
| Agent orchestration | ADK, Vertex AI Agent Engine, A2A | ❌ not implemented |
| Observability (partner) | OpenTelemetry OTLP → Grafana Cloud | ❌ not wired |
| Partner integration | Grafana Cloud MCP Server client | ❌ stub, no network I/O |
| Security | Cloud IAM, per-agent service accounts, Secret Manager | 🚧 script only |

### Grafana partner track

The track requires an active Grafana Cloud MCP Server client at runtime.
[ADR-008](adr/ADR-008-grafana-mcp-qualifying-use.md) records that decision and is written
in the future tense on purpose: **the client is not built yet.** What exists today is the
versioned "Shoot Mission Control" dashboard in `infra/grafana/dashboard-mission-control.json`
and its provisioning script. The MCP client, the OTLP exporter and the Annotations API
calls are all pending (EV-19, EV-20).

## Repository layout

```
src/        .NET solution: Domain, Application, Infrastructure, Solver,
            Mcp.* services, CallSheets, Web (Blazor)
agents/     Python agent layer (breakdown, sentinel, replanner) — see status above
tests/      xUnit: domain rules, solver, service contracts, call sheets
infra/      Grafana dashboard + provisioning, per-agent IAM setup
adr/        Architecture Decision Records (ADR-005, ADR-008, ADR-009)
demo/       Sample screenplay, demo harness, submission notes
```

## Quickstart

> Verified locally on Windows with the .NET 10 SDK and Python 3.12. The full cloud
> deployment path (Cloud Run, Cloud SQL, Agent Engine, Grafana provisioning) is not
> scripted yet — that is EV-31.

```bash
git clone https://github.com/hvaler/stripboard-dev.git && cd stripboard-dev

# Build and run the .NET test suite (24 tests)
dotnet test Stripboard.slnx

# Run the web UI at http://localhost:5164
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

# Replay a cached breakdown without calling the model
python -m agents.breakdown --file demo/screenplay.fountain --offline
```

Alternatively set `GEMINI_API_KEY` to use the Gemini Developer API instead of Vertex AI.

Python tests (the breakdown integration tests make real Gemini calls, and skip when no
credentials are configured):

```bash
python -m unittest discover -s agents/breakdown -p "test_*.py"
python -m unittest discover -s agents/sentinel  -p "test_*.py"
```

Optional, and requiring credentials you must supply yourself:

```bash
# Provision the Grafana dashboard (needs a Grafana Cloud token)
python infra/grafana/provision-dashboard.py

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
