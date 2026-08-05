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
> **Status verified**: 2026-08-05 · **Deadline**: 2026-09-07 14:00 PT
> **Evidence**: [`docs/EVIDENCE.md`](../docs/EVIDENCE.md) — runtime logs and figures behind every ✅ below

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
| **Text description** | Features, technologies, data sources, learnings. | §2 is now true throughout, with the learnings written from what actually broke. Ready to paste. | ✅ |

**Blocking for Stage One: none remaining.** Both mandatory technology requirements are met at
runtime — Google Cloud AI by EV-18 (ADR-009) and the Grafana MCP server by EV-19 (ADR-010) —
with the evidence recorded in [`docs/EVIDENCE.md`](../docs/EVIDENCE.md).

**One requirement is still outstanding: the video.** Everything it needs to show exists and
runs. Nothing else is blocking.

---

## 2. Devpost text submission

> Every row in the table below now says ✅ or states plainly what is not done. The one claim
> to keep watching is Agent Engine: the script exists and has not been run, and the copy says
> so. Do not upgrade it to "deployed" without deploying it.

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
| **Persistence** — Cloud SQL PostgreSQL with EF Core migrations | Applied at startup; data survives a redeploy (ADR-016) | ✅ |
| **Human-in-the-loop governance UI** — Blazor, side-by-side proposals, immutable audit trail | Driven by the solver: board, options and deltas all read from persisted versions | ✅ |
| **LLM breakdown agent** — parses screenplays into structured JSON scene objects | Gemini 2.5 Flash on Vertex AI from Fountain, Final Draft and PDF (multimodal); structured output, validation-retry loop, location/set separation | ✅ |
| **Conflict Sentinel** — active Grafana MCP Server client | Real MCP client; publishes annotations via `create_annotation` | ✅ |
| **Replanner agent** — alternative proposals with computed cost deltas | Google ADK agent whose single tool calls the CP-SAT solver; every figure it states is traceable to a solver run (ADR-017) | ✅ |
| **Orchestrator** — routes work between specialist agents | Google ADK root agent with no tools of its own, delegating to scheduler / replanner / governance; the governance agent's commit is refused with HTTP 403 (ADR-018) | ✅ |
| **Grafana drives the loop** — alerts on the shoot start a replan | Four rules over `shoot_*` metrics, read back over MCP and handed to the agents (ADR-019) | ✅ |
| **Our own MCP servers** — schedule, people, locations, weather | Real protocol on the official `ModelContextProtocol.AspNetCore` SDK: `initialize`, `tools/list` with generated schemas, `tools/call`. 33 contract tests drive the protocol (ADR-021). Not deployed | ✅ built · 🚧 hosted |
| **Governance you cannot talk your way past** — identity from the credential, not the payload | An agent sending `identity: "Producer"` is refused; a commit needs a platform-proved principal (ADR-020) | ✅ |
| **Mutation testing** — union rules | Stryker.NET, 100%, 21 killed, 0 survived (ADR-022) | ✅ |
| **Agent hosting** — Vertex AI Agent Engine | Deploy script written and passing preflight; **not deployed** — it is a billed resource | 🚧 |

### Partner integration (Grafana track)
1. **Grafana MCP Server client** (`agents/sentinel/grafana_mcp_client.py`) — ✅ MCP
   Streamable HTTP over JSON-RPC 2.0, hand-implemented, 73 tools discovered at runtime.
2. **Annotations through MCP** — ✅ disruptions published with the `create_annotation`
   tool call against Grafana Cloud and verified by reading them back.
3. **"Shoot Mission Control" dashboard** — ✅ versioned JSON plus a provisioning script
   that now fails loudly instead of reporting success on error.
4. **OpenTelemetry OTLP exporter** — ✅ traces and `shoot_*` production metrics stream to
   Grafana Cloud; every Mission Control panel queries them (ADR-014).
5. **Metrics-driven reasoning over MCP** — ✅ "Ask your shoot": Gemini discovers the MCP
   server's tools at runtime and answers questions from live Grafana data, with the
   queries shown beneath the answer. Deployed as a private multi-container Cloud Run
   service and answering from the public URL (ADR-015).
6. **Alerting on the shoot, read back over MCP** — ✅ four versioned rules over `shoot_*`
   metrics (union violations, a day hopping between locations, cast paid to wait, cost
   above budget). The Conflict Sentinel queries the firing ones with `alerting_manage_rules`
   and hands them to the agents, so *alert → options → approval* runs without a person
   starting it (ADR-019).

### Findings and learnings

Most of what we learned came from the same mistake wearing different clothes: **a system that
does not know something will happily say zero.**

- The replan options for an infeasible strategy returned `0 days, $0`. Zero reads as a
  measurement — "this option is free" — which is the opposite of "no schedule exists". They
  return `null` now.
- Two replan strategies came back with identical figures and the model dutifully recommended
  one "because it is the most cost-effective". They cost the same. Extending a window only
  *permits* extra days; the solver still minimises them. The honest answer — *you do not need
  the extra day* — was being dressed up as a choice.
- Every `shoot_*` metric read 0 in production while the service was serving a committed
  schedule. A gauge with no schedule to describe was publishing `0`, so the alert watching for
  union violations saw zero violations and stayed green. Silence that looks like health is the
  worst failure an alert can have. Gauges now publish nothing until there is something to say.
- Asked "what is the risk index?", the model answered 75. The real value was 54, and it had
  queried nothing. Forcing a tool call on the first turn and refusing any answer with zero
  tool calls fixed it. Grounding is a control-flow property, not a prompt.

The most useful failure came from running the loop end to end. Grafana fired "one day visits
four locations", the sentinel read it back over MCP, the replanner was handed it — and
correctly answered *there is nothing I can do*. It was right: that alert blocks no scene and
cancels no permit, so there were no scene-dates to re-solve around. **Not every alert is a
disruption.** Some are about the plan's quality, and the answer there is not to absorb
something but to price a constraint: cap the schedule at two locations a day and report the
cost in shooting days. Alerts now carry a label saying which of the two they want.

Building that surfaced a fourth zero-shaped bug, this time in a number that refused to move.
Consolidating a schedule left the company-move count unchanged, which made it look like pure
loss. The metric counted every location change including overnight relocations — travel that
happens between wrap and call, costs no shooting time, and that the solver itself does not
charge for. The figure on screen disagreed with the model that produced the schedule. Counting
only moves *within* a day makes the trade legible: 2 extra shooting days and $5,600 to remove
2 company moves. **A metric that cannot move is not a metric; it is decoration.**

The second lesson is about **where a rule has to live**. The obvious way to stop an agent
committing a schedule is to not give it the tool — which proves nothing, because the rule is
never tested. Our governance agent *has* the commit tool, is told to use it, and is refused
with HTTP 403 by the scheduling service. The check is in .NET behind an HTTP boundary, where
no prompt can argue with it.

The third is about **calibration**. Our first alert thresholds were guesses. The composite
"schedule risk index" never fired on any real schedule — our 14-scene demo scores 44 out of
100 while running a day that visits four locations. Lowering the threshold until it fired
would have taught us nothing, because "risk is 46" is not an instruction. We deleted that rule
and added a gauge for the thing a 1st AD can act on instead. A dashboard tolerates a
composite; an alert needs an action.

Two encoding bugs cost an afternoon between them and had the same shape — a default that
guesses. The C# compiler reads a BOM-less source file in the machine's ANSI codepage, so an
em-dash shipped as mojibake from a Spanish-locale Windows; `requests` decodes a `text/*` body
with no charset as ISO-8859-1, so every accented cast name came back from Grafana corrupted.
Both produced text that was *wrong* rather than *absent*, which is why neither failed loudly.

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
| **2:30 – 3:00** | Call sheets & Grafana dashboard | *"QuestPDF generates role-scoped call sheets, while Shoot Mission Control displays OTLP traces, solver metrics and the disruption timeline."* | ~~EV-20, EV-29~~ ✅ |

**Recut, now that EV-29 and EV-25 are real.** Lead with the differentiator instead of the
architecture. Every other entry in this track will point Grafana at its own latency; this one
points it at the shoot.

| Timestamp | Screen focus | Narration |
|---|---|---|
| **0:00 – 0:25** | Mission Control dashboard | *"This is not our application's latency. It is a film shoot: days left, budget burning, and which actors we are paying to sit in a trailer."* |
| **0:25 – 0:50** | `screenplay-nightfall.fountain` → stripboard | *"Gemini reads the screenplay into typed scenes. CP-SAT builds the schedule — union turnaround holds by construction, not by checking afterwards."* |
| **0:50 – 1:30** | Grafana alert firing → `run_alert_loop.py` | *"Grafana notices that a shooting day visits four locations and fires. No one is watching — the Conflict Sentinel reads that alert back through the Grafana MCP server and starts the replan itself."* |
| **1:30 – 2:15** | Orchestrator output, then Proposals | *"The orchestrator routes it. The replanner has no arithmetic: every figure comes from a CP-SAT run. Two options, real cost deltas — and when the two converge it says so rather than inventing a choice."* |
| **2:15 – 2:45** | The 403 | *"Now the agent tries to commit its own recommendation. The service refuses it. That check is in the scheduling service, not in a prompt — agents propose, humans decide."* |
| **2:45 – 3:00** | Producer commits → audit trail → call sheet PDF | *"The Producer approves. New version, new audit entry, call sheets out."* |

---

## 4. Judging criteria — honest self-assessment

Four criteria, 25% each.

| Criterion | Today | Why |
|---|:---:|---|
| Technological implementation | 9.5/10 | Solver, domain layer at 100% mutation score, Gemini breakdown from three screenplay formats, a hand-implemented MCP client *and* four MCP servers on the official SDK, an ADK agent tree whose root has no tools, governance that checks a credential rather than a claim, production telemetry the shoot itself emits with alert rules over it, and persistence that survives a redeploy. What is left is deployment: the MCP servers and the orchestrator run but are not hosted. |
| Design | 8/10 | Every page is driven by the engine, the hosted demo is stable, and the governance refusal is visible on screen rather than described. Still held back by schedules that pack too many locations into a day — now measured (`shoot_locations_per_day_max`) and alerted on rather than merely admitted. |
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
- Governance is enforced where a prompt cannot reach it — and where the *caller* cannot
  reach it either. Committing requires an identity the platform proved; a name in a request
  body is a claim, and a claim cannot commit (ADR-020).
- Grafana is a participant rather than a destination: rules over the shoot's own metrics
  fire, and the sentinel reads them back over MCP to start a replan.
- Stripboard is an MCP **server** as well as a client — four of them, official SDK, with 33
  contract tests that drive the protocol rather than the classes behind it (ADR-021).
- The union rules are verified by mutation testing: 100%, 21 mutants killed, 0 survived. It
  found two real gaps before it passed (ADR-022).
- 93 xUnit tests and 74 Python tests, green, including integration tests that run against
  the real Grafana Cloud stack and Vertex AI.
- Clean Architecture/DDD layering, conventional commits, ADRs.

**Do not claim:** Vertex AI Agent Engine deployment (the script exists and has not been
run), the A2A wire protocol (agents coordinate through ADK sub-agent transfer), per-agent
IAM enforced by Google (the accounts exist; the commit rule is enforced in the application),
or that the four MCP servers are deployed (they run locally and speak the protocol).

---

## 5. Pre-submission gate

Submit on **2026-09-06**, one day early — Devpost load at the deadline is a real risk.

- [x] Requirements matrix (§1) has no ❌ in a blocking row
- [x] Evidence of a real Gemini call recorded in `docs/EVIDENCE.md`
- [x] Evidence of a real Grafana MCP `tools/list` + ≥3 `tools/call` recorded
- [ ] Hosted URL survives a full cold demo run from an external network
- [ ] Quickstart reproduced on a clean machine
- [ ] Video ≤ 3:00, public, English subtitles
- [ ] Devpost form complete: description, repo, video, track
- [x] README contains no claim that the code does not support

### Before recording, restart what was scaled down

While waiting on the hackathon credits, the expensive services are stopped. The demo cannot
be recorded against a stopped database:

```bash
gcloud sql instances patch stripboard-db --activation-policy=ALWAYS --project stripboard-hack
gcloud run services update stripboard-web --min-instances=1 --project stripboard-hack --region europe-west1
bash infra/deploy-sentinel.sh          # if the sentinel was scaled to zero
```

Then confirm `/api/health` reports a committed schedule before pressing record.
