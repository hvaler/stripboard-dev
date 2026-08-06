# 🔄 Sequence Diagrams — Stripboard System Flows

> **Scope**: System Interaction Sequences (Fountain Script Import, Alert-Triggered Replan Loop, Producer Authorization)  
> **Format**: Mermaid Sequence Diagrams  
> **Language**: English

---

## 1. Screenplay Breakdown & Schedule Solving Sequence

```mermaid
sequenceDiagram
    autonumber
    actor Producer as 👤 Producer / 1st AD
    participant BreakdownAgent as 🤖 Breakdown Agent (Gemini 2.5)
    participant WebAPI as 🌐 Blazor Web API
    participant Importer as 📥 BreakdownImporter
    participant CPSAT as 🧮 CP-SAT Solver (.NET 10)
    participant Postgres as 🐘 Cloud SQL (PostgreSQL 16)
    participant OTLP as 📊 OpenTelemetry (ShootMetrics)

    Producer->>BreakdownAgent: Upload Fountain / PDF Script
    BreakdownAgent->>BreakdownAgent: Parse Scenes, Cast, Locations & Sets
    BreakdownAgent->>WebAPI: POST /api/breakdown/import (JSON Payload)
    WebAPI->>Importer: ImportAsync(json)
    Importer->>Postgres: Save Scenes, Cast, Sets & Locations
    WebAPI->>CPSAT: Solve(scenes, unionRules, dates)
    CPSAT-->>WebAPI: ScheduleBoard (Optimal Shoot Days & Metrics)
    WebAPI->>Postgres: Persist ScheduleVersion (IsCommitted = false)
    WebAPI->>OTLP: Observe(metrics)
    WebAPI-->>Producer: 200 OK (Schedule Board Rendered)
```

---

## 2. Grafana Cloud Alert & Autonomous Replan Loop Sequence

```mermaid
sequenceDiagram
    autonumber
    participant Grafana as 📈 Grafana Cloud
    participant Sentinel as 🛡️ Conflict Sentinel (Cloud Run)
    participant Orchestrator as 🎭 Orchestrator Agent (ADK)
    participant McpSchedule as 🔌 Stripboard.Mcp.Schedule
    participant ReplanService as ⚙️ ReplanService (CP-SAT)
    participant BlazorUI as 🖥️ Blazor UI (/replan)
    actor Producer as 👤 Human Producer

    Grafana->>Sentinel: 1. Alert Firing (Company Moves / Cast Overlap)
    Sentinel->>Orchestrator: 2. Trigger Replan Request
    Orchestrator->>McpSchedule: 3. Query Active Schedule (JSON-RPC 2.0)
    McpSchedule-->>Orchestrator: Schedule Version & Metrics
    Orchestrator->>ReplanService: 4. POST /api/replan or Consolidate()
    ReplanService-->>Orchestrator: Alternative Schedule Proposals & Cost Deltas
    Orchestrator->>BlazorUI: 5. Display Replan Alternatives
    Producer->>BlazorUI: 6. Click "Commit Schedule"
    BlazorUI->>BlazorUI: Validate Producer Claim (CallerIdentity)
    BlazorUI->>Grafana: 7. Publish MCP Annotation (create_annotation)
```

---

## 3. Producer Authorization vs Agent Self-Commit Rejection Sequence

```mermaid
sequenceDiagram
    autonumber
    actor Agent as 🤖 AI Agent / Attacker
    actor Producer as 👤 Authorized Producer
    participant API as 🔒 /api/schedule/commit
    participant Auth as 🛡️ AgentAuthorizationService
    participant Service as ⚙️ ScheduleService

    alt Agent Self-Commit Attempt
        Agent->>API: POST /api/schedule/commit { identity: "Producer" }
        API->>Auth: Resolve Caller Identity (Platform Claims)
        Auth-->>API: Resolved Identity = "Agent" (Claim overridden)
        API->>Service: CommitAsync(versionId, "Agent")
        Service-->>API: Throw NotAuthorizedException (HTTP 403)
        API-->>Agent: 403 Forbidden ("Only Producer principal may commit")
    else Human Producer Approval
        Producer->>API: POST /api/schedule/commit { identity: "Producer" }
        API->>Auth: Resolve Caller Identity (Platform Claims)
        Auth-->>API: Resolved Identity = "Producer"
        API->>Service: CommitAsync(versionId, "Producer")
        Service-->>API: 200 OK (Schedule Version Committed)
    end
```
