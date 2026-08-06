# Stripboard — pitch deck

Ten slides for the Agentic Cinema submission (Grafana partner track). Every figure below was
measured on the deployed stack on 2026-08-05 and is reproducible from
[`docs/EVIDENCE.md`](../docs/EVIDENCE.md). If a number changes, change it here too — a deck
that drifts from the product is the thing this project spent its whole life eliminating.

Paste one slide per section into any deck tool. The `>` lines are speaker notes, not slide text.

---

## 1 — Stripboard

**An autonomous line producer for film shoots.**

The LLM formulates. The solver decides. A human approves.

> Do not open with architecture. Open with the sentence above — it is the whole idea, and it
> is the part no other entry will have.

---

## 2 — The problem

A shooting schedule is a constraint problem with legal consequences.

- Scenes grouped by location — every company move costs an hour of daylight
- Cast availability (Day Out of Days) is non-negotiable
- Union rules are law: 12-hour turnaround, meal penalties, night→day transitions
- Permit windows, daylight hours, weather on exteriors

An actor calls in sick at 6am. The 1st AD replans **by hand, overnight**, then redistributes
call sheets to the entire crew.

> This is not a hypothetical. It is the job.

---

## 3 — Why not just ask a model

A model asked to build a shooting schedule will produce one that looks right and breaks
union law. There is no way to tell from the output.

So the model never schedules:

| Step | Who | Why |
|---|---|---|
| Read the screenplay | **Gemini 2.5 Flash** | Language is what models are for |
| Build the schedule | **CP-SAT (OR-Tools)** | Deterministic, provably feasible |
| Enforce union rules | **C# domain code** | Tested, not prompted |
| Commit it | **A human Producer** | Legal and financial consequence |
| Explain the options | **Gemini** | Grounded in solver output only |

The agents have no arithmetic available to them. There is nothing to hallucinate with.

---

## 4 — Architecture

```
Grafana Cloud  ──alert──▶  Conflict Sentinel  ──▶  Orchestrator (ADK)
     ▲                     (MCP client)              │
     │ OTLP                                          ├─ scheduler
     │                                               ├─ replanner  ──▶ CP-SAT
Blazor on Cloud Run  ◀── Cloud SQL ◀── solver        └─ governance ──▶ 403
     │
     └── a human Producer approves
```

- **Google Cloud AI only**: `google-genai`, `google-adk`. No third-party AI SDK in the repo.
- **Partner integration**: a hand-written MCP client — Streamable HTTP over JSON-RPC 2.0 —
  against the official `grafana/mcp-grafana` server. **73 tools discovered at runtime.**

---

## 5 — Gemini reads the screenplay

An original 14-scene screenplay the model has never seen:

```
source=gemini   model=gemini-2.5-flash   attempts=1   scenes=14

 8  SALFORD SORTING OFFICE   MANAGER'S OFFICE   INT  DAY   4/8  Halliwell, Reyes
12  MANCHESTER SHIP CANAL    MAINTENANCE HUT    INT  NIGHT 3/8  Maeve Okonkwo
```

Three details that matter:

- **`source=gemini`, not `fallback`.** The deterministic parser is a labelled last resort, and
  the output always says which one produced it.
- **Location separated from set.** A room inside a place the trucks already reached is not a
  company move. That distinction is what makes the cost real.
- **Page length is computed, not guessed.** The model extracts; Python measures the eighths.

Feed a different screenplay and the board on screen changes. Nothing is hardcoded.

---

## 6 — The differentiator: Grafana watches the shoot, not the app

Every other entry in this track will point Grafana at its own request latency.

A film shoot **is** a real-time system: a budget burning down, a risk profile that moves, and
actors paid whether or not they are called. So that is what we export over OTLP:

| Metric | Live value |
|---|---|
| `shoot_days_total` | 2 |
| `shoot_cost_estimate_usd` | 26,800 |
| `shoot_company_moves` | 6 |
| `shoot_locations_per_day_max` | **4** |
| `shoot_union_violations` | 0 |
| `shoot_cast_utilization{actor="Clerk"}` | **0.5** |

That last one is money. An actor called on half the shooting days is a contract paid against
days not worked — the exact waste a Day Out of Days schedule exists to prevent.

---

## 7 — Grafana starts the loop. Nobody is watching.

Four alert rules over those metrics, versioned in the repo. One is firing:

```
[1/3] Sentinel asking Grafana which rules are firing…
   -> [high] Unit hopping between locations in a day

[2/3] handled by: replanner
      tool: consolidate_schedule({'max_locations_per_day': 2})

      Leave it — the worst day visits 4 locations   2 days  6 moves  $26,800
      Consolidate — at most 2 locations a day       4 days  4 moves  $29,600
                                                            (+2 days, +$2,800)
```

**A schedule-quality alert is not a disruption.** Nothing is blocked, so there is nothing to
absorb — only a constraint to price. Both figures are separate CP-SAT runs.

> This is the money shot of the video. Sit on it.

---

## 8 — The agent asks to commit. The system says no.

```
[3/3] The agent tries to commit its own recommendation…
   -> committed=False
      'sa-stripboard-replanner' cannot commit a schedule.
      Only the Producer role may commit — agents propose, humans decide.
```

The obvious way to stop an agent committing is to **not give it the tool.** That proves
nothing: a rule never tested is not a rule.

So the governance agent **has** the commit tool, is instructed to use it, and is refused with
**HTTP 403** by the scheduling service — a check in .NET, behind an HTTP boundary, where no
prompt can argue with it.

And it cannot lie its way past either. The identity comes from the **credential on the
request** — a Cloud Run identity token Google validated — not from the payload. Sending
`identity: "Producer"` changes nothing:

> *'Producer' claims the Producer role but nothing verified it. A commit requires an
> authenticated caller — an identity supplied in the request body is a claim, not a
> credential.*

That was a real hole until we closed it: the rule was being checked against a string the
caller wrote itself.

The root orchestrator has **no tools at all**. It routes and answers nothing, so it cannot
state a figure a specialist did not produce.

---

## 9 — What is real, and what is not

Judges check. So this is on the front page of the README, not buried.

**Real, and reproducible from `docs/EVIDENCE.md`:**
Gemini on Vertex AI · 73 Grafana MCP tools live · four MCP servers of our own on the official
SDK · annotations written *through* MCP and read back · alert rules firing · CP-SAT with
DOOD, day/night units and hard location caps · union rules at 100% mutation score ·
Cloud SQL persistence · 94 xUnit + 83 Python tests green

**Not done, and said so:**
the orchestrator runs but is not hosted — Agent Engine's deploy script is written and
deliberately unrun, because it is billed · no A2A wire protocol · the replanner reaches the
engine over REST rather than MCP

> Bugs shipped and were fixed along the way, nearly all the same shape: **a success that was
> not one.** A gauge publishing 0 when it knew nothing, so the union-violation alert stayed
> green. A deploy script that deployed nothing and said it had. A metric that could not move.
> A governance check reading the caller's own word for who they were. Mutation testing found
> two union rules that were never actually being tested. Zero is a measurement — "I don't
> know" is not zero, and neither is "they said so".

---

## 10 — Impact

A mid-budget feature loses **$30–50k a day** to a schedule that breaks. The replan is done by
one exhausted person overnight, and its cost is invisible until the money is spent.

Stripboard makes the trade explicit: *two extra shooting days and $2,800 to stop moving the
unit four times a day.* Then it stops, and waits for a human.

**Not "AI runs your production."** AI reads, computes and explains; a producer decides.

🎬 <https://stripboard-web-wc7oib7k6q-ew.a.run.app>
