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
![Python ADK](https://img.shields.io/badge/agents-Gemini%20%2B%20ADK-4285F4)
![Grafana](https://img.shields.io/badge/observability-Grafana%20Cloud-F46800)
![License](https://img.shields.io/badge/license-Apache--2.0-green)

## The problem

A shooting schedule is a brutal constraint-satisfaction problem: scenes must be grouped
by location to minimize company moves, cast availability (Day Out of Days) must be
honored, union rules enforced (12-hour turnaround, meal penalties, night-to-day
transitions), permit windows and daylight hours respected. It breaks constantly — an
actor gets sick, weather turns, a permit falls through — and the 1st Assistant Director
replans the whole thing by hand, often overnight, then redistributes call sheets to the
entire crew.

## How it works

```
                     ┌─────────────────────────────┐
                     │   Orchestrator (A2A)        │
                     │   Vertex AI Agent Engine    │
                     └─────────────┬───────────────┘
        ┌──────────┬───────────────┼───────────────┬────────────┐
   Breakdown   Scheduler      Replanner       Call sheets   Watchers
   (Gemini →   (formulates    (options +      (role-scoped  (availability,
    typed       constraints)   cost deltas)    PDFs)         locations,
    scenes)         │                                         weather)
                    ▼                                            │
             CP-SAT solver (Google OR-Tools)              Conflict Sentinel
             deterministic, tested                        (read-only, typed
                    │                                      anomalies →
                    ▼                                      Grafana annotations)
        ┌───────────────────────────────────────────┐
        │  MCP layer (Cloud Run): mcp-schedule ·    │
        │  mcp-people · mcp-locations · mcp-weather │
        └───────────────────────────────────────────┘
```

Design principles:

- **The LLM never "reasons" schedules.** Gemini extracts, formulates and explains; a
  deterministic CP-SAT solver computes; union rules live in the domain layer as tested
  code, not in prompts.
- **Nothing commits without a human.** The replanner proposes ranked options with cost
  deltas; only the Producer role can commit a schedule version.
- **Append-only versioning.** Every replan is a new `ScheduleVersion` with its parent,
  author (human or agent) and triggering disruption — the audit trail is free.
- **Least-privilege agents.** Each agent runs under its own service account via Workload
  Identity: the sentinel physically cannot write; the replanner cannot commit.

## Google Cloud + Grafana integration

| Piece | Technology |
|---|---|
| Agents & orchestration | Gemini, ADK, Vertex AI Agent Engine, A2A |
| Solver | Google OR-Tools CP-SAT (.NET bindings) |
| Services & MCP servers | ASP.NET Core (.NET 10) on Cloud Run, MCP C# SDK |
| Data | Cloud SQL (PostgreSQL), EF Core, GCS (signed URLs for call sheets) |
| Security | Cloud IAM, per-agent service accounts, Workload Identity, Secret Manager |
| **Observability (partner)** | OpenTelemetry (OTLP) → **Grafana Cloud**; the sentinel calls the **Grafana Annotations API** on every anomaly; alerting on critical severity; the "Shoot Mission Control" dashboard is provisioned via API from versioned JSON in `infra/grafana/` |

## Repository layout

```
src/        .NET solution: Domain, Application, Infrastructure, Solver,
            Mcp.* servers, CallSheets, Web (Blazor)
agents/     Thin Python/ADK agent layer (breakdown, scheduler, watchers,
            sentinel, replanner, orchestrator)
tests/      xUnit: domain rules, solver golden cases, MCP contract tests
infra/      IaC: service accounts, Cloud Run, Cloud SQL, Grafana provisioning
adr/        Architecture Decision Records (ADR-001..007)
demo/       Sample screenplay, seed data, demo script
```

## Quickstart

> Full instructions verified on a clean machine before submission.

```bash
# Prerequisites: .NET 10 SDK, Python 3.12+, gcloud CLI, a GCP project with billing,
# a Grafana Cloud stack (free tier)

git clone https://github.com/hvaler/stripboard.git && cd stripboard

# 1. Infrastructure (service accounts, Cloud SQL, secrets)
./infra/setup.sh <gcp-project-id>

# 2. Database
dotnet ef database update --project src/Stripboard.Infrastructure
dotnet run --project demo/seed

# 3. Deploy services + MCP servers to Cloud Run
./infra/deploy.sh

# 4. Deploy agents to Vertex AI Agent Engine
cd agents && ./deploy.sh

# 5. Provision the Grafana dashboard + alert
./infra/grafana/provision.sh

# 6. Run the demo: inject a disruption and watch the replan
./demo/inject-disruption.sh
```

## The 3-minute demo

[Video link — coming with submission]

1. Drop a screenplay → typed breakdown appears scene by scene
2. The stripboard builds itself; solver metrics on screen
3. **The star scene:** the lead actor becomes unavailable → 7 scenes flagged in
   seconds → two replan options with cost deltas → Producer approves → call sheets
   regenerate in cascade
4. Governance: a crew member sees only their own call sheet; the disruption appears as
   a Grafana annotation with the alert firing; the full audit trail

## Development notes

This project is developed primarily with AI assistance (Google Antigravity) under an
internal engineering standard (Clean Architecture/DDD, tested domain rules,
conventional commits, ADRs). All runtime AI in the product is Gemini on Google Cloud.

## License

[Apache-2.0](LICENSE)
