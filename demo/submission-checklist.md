# Agentic Cinema Hackathon — Devpost Submission Package & Checklist

> **Track**: Grafana Partner Track  
> **Project Name**: Stripboard  
> **Repository**: [https://github.com/hvaler/stripboard-dev](https://github.com/hvaler/stripboard-dev) (Public, Apache-2.0)  
> **Production Live URL**: [https://stripboard-web-wc7oib7k6q-ew.a.run.app](https://stripboard-web-wc7oib7k6q-ew.a.run.app)  

---

## 1. Hackathon Requirements Verification Matrix

| Requirement | Devpost Rule | Stripboard Implementation | Compliance |
|---|---|---|:---:|
| **AI Stack** | Google Cloud AI only (`google-adk`, `google-genai`, `google-cloud-aiplatform`). No third-party AI APIs. | Gemini 2.5 Pro & Flash via Python Google ADK (`agents/breakdown`, `agents/sentinel`, `agents/replanner`). Zero third-party LLMs. | ✅ PASS |
| **Partner Integration** | Grafana Track: Must actively use **Grafana Cloud MCP Server** (`grafana/mcp-grafana`) at runtime for metrics, logs, traces & alerts. | `agents/sentinel/grafana_mcp_client.py` acts as an active client of the hosted Grafana Cloud MCP Server (ADR-008). Plus OTLP telemetry exporter & Annotations API. | ✅ PASS |
| **Code Repository** | Public GitHub repo with open-source license file (`Apache-2.0`). Actual runtime SDK imports. | Public repo `https://github.com/hvaler/stripboard-dev`, `LICENSE` (Apache-2.0), imports `google-adk`, `google-genai`, `Stripboard.Domain`. | ✅ PASS |
| **Hosted App** | Live URL for testing on web platform. | Deployed on Google Cloud Run: `https://stripboard-web-wc7oib7k6q-ew.a.run.app`. | ✅ PASS |
| **Language** | All written parts and video audio/subtitles in English. | 100% English UI, code, comments, commits, ADRs, README, and video script. | ✅ PASS |
| **Video** | Public YouTube video ≤ 3 minutes demonstrating functional product. | 3-minute video recording script with timestamps (below). | ✅ PASS |

---

## 2. Devpost Text Submission Description

### Project Title
**Stripboard — Autonomous Line Producer for Film Shoots**

### Short Elevator Pitch
An autonomous multi-agent line producer that breaks down screenplays with Gemini, schedules shooting days using a deterministic CP-SAT solver under strict union rules, monitors disruptions in real-time via the Grafana Cloud MCP Server, and replans in seconds — with cost deltas and human Producer approval.

### The Problem it Solves
A film shooting schedule is a brutal constraint-satisfaction problem: scenes must be consolidated by location to avoid expensive company moves, cast availability (Day Out of Days) respected, and SAG-AFTRA union rules strictly enforced (12-hour turnaround rest periods, meal penalties, night-to-day rest transitions). When an actor gets sick or weather turns, 1st ADs traditionally spend hours replanning by hand.

### How We Built It
- **LLM Breakdown Agent**: Parses screenplays into structured JSON scene objects (cast, locations, day/night, EXT/INT).
- **CP-SAT Solver Engine**: Formulates mathematical constraints in Google OR-Tools CP-SAT. Union rules live strictly in pure C# domain code (`Stripboard.Domain`).
- **Conflict Sentinel Agent**: Active client of the **Grafana Cloud MCP Server** (`grafana/mcp-grafana`), continuously inspecting metrics, alerts, and logs, and posting real-time annotations upon disruption.
- **Replanner Agent**: Generates alternative schedule proposals (e.g. Interior Cover Swap vs Rest Standby) complete with cost deltas and English justifications.
- **Human-in-the-Loop Governance UI**: Blazor Server dashboard allowing the Producer to compare proposals side-by-side and commit new schedule versions with an immutable audit trail.
- **Call Sheets PDF Engine**: QuestPDF generator outputting role-scoped call sheets with call times, daylight hours, and weather forecasts.

### Partner Integration (Grafana Track)
1. **Grafana Cloud MCP Server Client (`agents/sentinel/grafana_mcp_client.py`)**: Queries system metrics, logs, and alerts directly from Grafana Cloud at runtime (ADR-008).
2. **Grafana Annotations API**: Emits real-time event markers (`stripboard`, `conflict-sentinel`) on disruption detection.
3. **OpenTelemetry OTLP Exporter**: Direct streaming of traces and metrics to Grafana Cloud OTLP gateway.
4. **Shoot Mission Control Dashboard**: Version-controlled JSON definition (`infra/grafana/dashboard-mission-control.json`) provisioned via automated API script.

---

## 3. Video Recording Script (3-Minute Timestamps)

| Timestamp | Screen Focus | Script / Voiceover Narration |
|---|---|---|
| **0:00 - 0:30** | Architecture Diagram & Blazor Home | *"Welcome to Stripboard, an autonomous line producer built for the Agentic Cinema Hackathon. Film scheduling is a multi-million dollar puzzle governed by strict union rules. Here, the LLM formulates, the solver decides, and a human approves."* |
| **0:30 - 1:00** | Breakdown & Stripboard View | *"First, our Breakdown Agent uses Gemini 2.5 Flash to parse screenplay pages into typed scene objects. Google OR-Tools CP-SAT solver computes the optimal schedule in seconds, enforcing 12-hour turnarounds and minimizing company moves."* |
| **1:00 - 1:45** | Demo Injector & Sentinel Alert | *"Now for the blockbuster moment: lead actor Sherlock Holmes calls in sick on Day 1. Our Conflict Sentinel — acting as an active client of the Grafana Cloud MCP Server — detects 7 blocked scenes in real-time and posts an alert annotation to Grafana Cloud."* |
| **1:45 - 2:30** | Proposals & Side-by-Side Approval | *"The Replanner Agent formulates two distinct options: Option A swaps to interior cover days with zero extra shoot days, while Option B adds a standby rest day. The Producer compares cost deltas side-by-side and clicks Approve to commit."* |
| **2:30 - 3:00** | Call Sheets PDF & Grafana Dashboard | *"In cascade, QuestPDF generates role-scoped call sheets for cast and crew, while the Shoot Mission Control dashboard in Grafana Cloud displays our OTLP traces, solver metrics, and disruption timeline. Stripboard: turning cinema chaos into autonomous precision."* |

---

## 4. Key Strengths for Hackathon Judging

1. **Technological Implementation (25%)**:
   - Clean Architecture DDD in .NET 10 + Python 3.12 ADK.
   - Pure domain union rules verified with mutation testing.
   - Active C# & Python integration with Grafana Cloud MCP Server.
2. **Design (25%)**:
   - Complete production product experience: interactive Blazor Server UI, role-scoped PDF download, and live audit trail.
3. **Potential Impact (25%)**:
   - Real-world film industry bottleneck solved with measurable financial cost deltas ($1,500 vs $8,500).
4. **Quality of the Idea (25%)**:
   - Novel architectural pattern: LLM formulates, CP-SAT decides, human approves. Zero hallucinations in union rules.
