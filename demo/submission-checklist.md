# Agentic Cinema Hackathon — Devpost Submission Prep

> ⚠️ **This is a working checklist, not a compliance certificate.** Nothing here is marked
> verified until it has been demonstrated at runtime and the evidence recorded.
> **Both mandatory technology requirements are now met at runtime.** What remains is
> product quality and the submission artefacts, not eligibility.
>
> **Track**: Grafana Partner Track
> **Project Name**: Stripboard
> **Repository**: [https://github.com/hvaler/stripboard-dev](https://github.com/hvaler/stripboard-dev) (Public, Apache-2.0)
> **Deployment**: [https://stripboard-web-wc7oib7k6q-ew.a.run.app](https://stripboard-web-wc7oib7k6q-ew.a.run.app) — ✅ stable, full walkthrough verified in a browser with zero console errors (ADR-011)
> **Status verified**: 2026-08-04 · **Deadline**: 2026-09-07 14:00 PT

---

## 1. Requirements matrix

Legend: ✅ verified at runtime · 🚧 partially built · ❌ not implemented

| Requirement | Devpost rule | Where we actually are | Status |
|---|---|---|:---:|
| **AI stack** | Google Cloud AI only (`google-adk`, `google-genai`, `google-cloud-aiplatform`). No third-party AI APIs. | `agents/breakdown` calls **Gemini 2.5 Flash on Vertex AI** via `google-genai`, using native structured output (ADR-009). Verified against project `stripboard-hack`; integration tests make real calls. Zero third-party AI — no Anthropic, OpenAI or LangChain. | ✅ |
| **Partner integration** | Grafana track: must **actively use the Grafana Cloud MCP Server** (`grafana/mcp-grafana`) at runtime. A README reference is explicitly insufficient. | `agents/sentinel/grafana_mcp_client.py` implements the MCP Streamable HTTP transport over JSON-RPC 2.0 against the official `grafana/mcp-grafana` server: real `initialize` handshake with session negotiation, **73 tools** from `tools/list` against the project's Grafana Cloud stack, and disruptions published as annotations via the `create_annotation` tool call and read back with `get_annotations` (ADR-010). | ✅ |
| **Runtime usage, not imports** | Repo must show Google Cloud and partner services *called* in code. | Both are called at runtime and both are covered by integration tests that fail — not skip — when the service is configured but broken. | ✅ |
| **Code repository** | Public repo, open-source license file. | Public, `LICENSE` (Apache-2.0) present at repo root. | ✅ |
| **Project novelty** | Created during the contest period (from 2026-07-27). | First commit 2026-08-03. | ✅ |
| **Platform** | Must run on web, Android or iOS. | Blazor web app on Cloud Run, configured for a stateful SignalR circuit (ADR-011). | ✅ |
| **Hosted project URL** | Working, accessible demo. | Full demo walkthrough driven end to end in a browser: disruption → two costed options → governance refusal for an agent → Producer commit → audit trail. Zero console errors. | ✅ |
| **Language** | All written parts and video audio/subtitles in English. | Code, comments, commits, ADRs, README and UI are English. | ✅ |
| **Demo video** | Public YouTube/Vimeo video ≤ 3 minutes of the working product. | Script drafted (§3). Not recorded. | ❌ |
| **Text description** | Features, technologies, data sources, learnings. | Draft in §2, currently describes unbuilt features. | 🚧 |

**Blocking for Stage One: none remaining.** Both mandatory technology requirements are now
met at runtime — Google Cloud AI by EV-18 (ADR-009) and the Grafana MCP server by EV-19
(ADR-010). EV-21 then wired the UI to that engine, so the product demonstrates itself.
What is left is not eligibility but polish: a stable hosted demo, telemetry, and the video.

---

## 2. Devpost text submission (DRAFT — not submittable yet)

> ⚠️ The copy below describes the **target** product. Several claims are not true today.
> Do not paste this into Devpost until the matrix above is green; strike anything still ❌.

### Project title
**Stripboard — Autonomous Line Producer for Film Shoots**

### Elevator pitch
An autonomous multi-agent line producer that breaks down screenplays with Gemini, schedules
shooting days using a deterministic CP-SAT solver under strict union rules, monitors
disruptions in real time via the Grafana Cloud MCP Server, and replans in seconds — with
cost deltas and human Producer approval.

### The problem it solves
A film shooting schedule is a brutal constraint-satisfaction problem: scenes must be
consolidated by location to avoid expensive company moves, cast availability (Day Out of
Days) respected, and union rules strictly enforced (12-hour turnaround rest periods, meal
penalties, night-to-day rest transitions). When an actor gets sick or weather turns, 1st ADs
traditionally spend hours replanning by hand. *(This section is accurate.)*

### How we built it

| Component | Claim | True today? |
|---|---|:---:|
| **CP-SAT solver engine** — constraints in Google OR-Tools; union rules as pure C# domain code | Yes, and it is the strongest part of the project | ✅ |
| **Call sheets PDF engine** — QuestPDF, role-scoped | Yes | ✅ |
| **Human-in-the-loop governance UI** — Blazor, side-by-side proposals, immutable audit trail | Driven by the solver: board, options and deltas all read from persisted versions | ✅ |
| **LLM breakdown agent** — parses screenplays into structured JSON scene objects | Gemini 2.5 Flash on Vertex AI, structured output, validation-retry loop | ✅ |
| **Conflict Sentinel** — active Grafana MCP Server client | Real MCP client; publishes annotations via `create_annotation` | ✅ |
| **Replanner agent** — alternative proposals with computed cost deltas | Each option is a separate CP-SAT run; deltas are differences between solved schedules. Still .NET, not yet an ADK agent (EV-24) | 🚧 |

### Partner integration (Grafana track)
1. **Grafana MCP Server client** (`agents/sentinel/grafana_mcp_client.py`) — ✅ MCP
   Streamable HTTP over JSON-RPC 2.0, hand-implemented, 73 tools discovered at runtime.
2. **Annotations through MCP** — ✅ disruptions published with the `create_annotation`
   tool call against Grafana Cloud and verified by reading them back.
3. **"Shoot Mission Control" dashboard** — ✅ versioned JSON plus a provisioning script
   that now fails loudly instead of reporting success on error.
4. **OpenTelemetry OTLP exporter** — ❌ packages declared but not wired, EV-20.
5. **Metrics-driven reasoning over MCP** — ❌ EV-29.

### Findings and learnings
*To be written from actual experience before submission — this is a required field.*

---

## 3. Video recording script (3 minutes)

> Each row lists what must be **true and demonstrable** before it can be narrated. A row
> whose precondition is unmet gets cut or rewritten — the video may only show real behaviour.

| Timestamp | Screen focus | Narration | Precondition |
|---|---|---|---|
| **0:00 – 0:30** | Architecture diagram & Blazor home | *"Film scheduling is a multi-million dollar puzzle governed by strict union rules. Here, the LLM formulates, the solver decides, and a human approves."* | ~~EV-21~~ ✅ |
| **0:30 – 1:00** | Breakdown & stripboard view | *"Our Breakdown Agent uses Gemini to parse screenplay pages into typed scene objects. Google OR-Tools CP-SAT computes the optimal schedule, enforcing 12-hour turnarounds and minimizing company moves."* | ~~EV-18~~ ✅ · EV-21, EV-27 |
| **1:00 – 1:45** | Disruption & sentinel alert | *"The lead actor calls in sick. Our Conflict Sentinel — an active client of the Grafana MCP Server — detects the blocked scenes and posts an alert annotation to Grafana."* | ~~EV-19~~ ✅ · EV-20 for the metrics panel |
| **1:45 – 2:30** | Proposals & approval | *"The Replanner formulates two options with real cost deltas. The Producer compares them side by side and commits."* | ~~EV-21~~ ✅ (deltas are real; EV-24 makes the replanner an ADK agent) |
| **2:30 – 3:00** | Call sheets & Grafana dashboard | *"QuestPDF generates role-scoped call sheets, while Shoot Mission Control displays OTLP traces, solver metrics and the disruption timeline."* | EV-20, EV-29 |

**Suggested revision once EV-29 lands:** lead with the differentiator — Grafana observing
*the shoot itself* (cost burn, cast utilisation, schedule risk) rather than the application.
That is the part no other entry in this track is likely to show.

---

## 4. Judging criteria — honest self-assessment

Four criteria, 25% each.

| Criterion | Today | Why |
|---|:---:|---|
| Technological implementation | 8/10 | Solver, domain layer, Gemini breakdown, a hand-implemented MCP client, and a UI driven end to end by the engine. What holds it back is in-memory persistence, no OTLP export, and REST rather than MCP on our own services. |
| Design | 7/10 | Every page is driven by the engine, the hosted demo is stable and the governance story is visible on screen. Held back by crude scheduling output (a day still hops between eight locations) until EV-27. |
| Potential impact | 8/10 | The problem is real, specific and expensive; the framing is credible to a 1st AD. |
| Quality of the idea | 9/10 | *"The LLM formulates, the solver decides, a human approves"* is a non-obvious architecture that removes hallucination risk from a domain with legal and financial consequences. |

**Strengths that are real and defensible:**
- Deterministic CP-SAT scheduling instead of asking a model to produce a schedule.
- Union rules as tested domain code (12h turnaround with midnight crossing, meal penalties,
  night→day transitions) rather than as prompt text.
- Gemini structured output for the breakdown, with page length (`eighths`) computed
  deterministically rather than guessed by the model — the guiding principle applied at the
  smallest scale.
- Failure is labelled, not hidden: every breakdown carries `source`/`model`/`attempts`, the
  fallback returns empty cast rather than inventing one, and a disruption that was not
  published to Grafana says `published=False` instead of implying success.
- The MCP transport is implemented rather than imported — session negotiation, SSE and
  JSON response handling, paginated tool discovery — in ~200 lines with one dependency.
- 48 xUnit tests and 27 Python tests, green, including integration tests that run against
  the real Grafana Cloud stack and Vertex AI.
- Clean Architecture/DDD layering, conventional commits, ADRs.

**Do not claim:** mutation testing (not configured), Vertex AI Agent Engine, A2A
orchestration, Cloud SQL, OTLP telemetry, or that the Blazor UI is driven by the solver
and the agents. Note the project exposes *no* MCP servers of its own yet — the four
`Stripboard.Mcp.*` services are REST (EV-23); it is an MCP **client**.

---

## 5. Pre-submission gate

Submit on **2026-09-06**, one day early — Devpost load at the deadline is a real risk.

- [ ] Requirements matrix (§1) has no ❌ in a blocking row
- [ ] Evidence of a real Gemini call recorded in `docs/EVIDENCE.md`
- [ ] Evidence of a real Grafana MCP `tools/list` + ≥3 `tools/call` recorded
- [ ] Hosted URL survives a full cold demo run from an external network
- [ ] Quickstart reproduced on a clean machine
- [ ] Video ≤ 3:00, public, English subtitles
- [ ] Devpost form complete: description, repo, video, track
- [ ] README contains no claim that the code does not support
