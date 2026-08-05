# ADR-019 — Alerting on the shoot, and what a gauge says when it knows nothing

**Status:** Accepted · 2026-08-05 · Completes EV-29 · Builds on [ADR-014](ADR-014-observing-the-shoot.md)

## Context

ADR-014 exported metrics about the production rather than about the software — days, company
moves, cost, union violations, risk, cast utilisation. They were exported and then nothing
read them. A dashboard nobody is watching at 5am is decoration.

The loop this project claims is:

```
the shoot emits signal -> Grafana alerts -> the agent reads Grafana over MCP
  -> Gemini formulates -> CP-SAT decides -> a human approves
```

Every arrow existed except the second and third.

## Decision

### Rules live in the repository, not in a browser

`infra/grafana/alert-rules.json` holds four rules; `infra/grafana/provision-alerts.py` pushes
them through the Grafana provisioning API and can remove them again. Consistent with
[ADR-010](ADR-010-grafana-mcp-sidecar-transport.md): infrastructure setup uses the HTTP API,
runtime uses MCP.

| Rule | Fires on | Why a 1st AD cares |
|---|---|---|
| Union violation in the committed schedule | `shoot_union_violations > 0` | Legal exposure, not a preference |
| Unit hopping between locations in a day | `shoot_locations_per_day_max > 2` | A day largely spent in the van |
| Cast paid to wait | `min(shoot_cast_utilization) < 0.34` | A contract paid against days not worked |
| Schedule cost above budget | `shoot_cost_estimate_usd > 45000` | The budget line, beside the schedule |

Each carries a `stripboardTrigger` label. That label is the contract between Grafana and the
Conflict Sentinel: it says which kind of disruption to open when the rule fires. The
provisioner refuses to create a rule without one, because a rule that can fire but cannot be
acted on is a notification, not an alert.

`X-Disable-Provenance` keeps the rules editable in the UI. During a shoot, a 1st AD must be
able to silence a rule at 5am without a redeploy.

### The risk index is on the dashboard and not in the alerts

The first draft alerted on `shoot_risk_index > 60`. Nothing ever reached it. The 14-scene
demo schedule scores **44** — while running a shooting day that visits **four locations**,
which any 1st AD would call a bad day.

The tempting fix was to lower the threshold to 45. It would have fired on that schedule and
taught nobody anything, because a composite index cannot say what to do about itself. "Risk
is 46" is not an instruction. "Day 1 visits four locations" is.

So the index keeps its dashboard gauge — a single number meaning *this shoot is tight* is
worth glancing at — and a new gauge was added for the thing that is actually actionable:

```
shoot.locations_per_day_max   locations visited on the worst day of the shoot
```

The maximum, not the average: one good day does not make up for a day spent in the van, and
an average would hide exactly the day that hurts. It reads **4** in production, and its rule
fires.

The general lesson is that alert thresholds have to be calibrated against output the system
actually produces. Ours were guesses until there was something to compare them to.

### The sentinel asks Grafana, rather than only writing to it

`ConflictSentinelAgent.firing_alerts()` calls `alerting_manage_rules` over MCP with the
selector `{stripboard="true"}` and returns the firing ones. `demo/run_alert_loop.py` runs the
whole chain: alert → orchestrator → CP-SAT options → the agent's own commit refused.

When nothing is firing it says *"no rule crossed its threshold"*, not *"no disruptions"*. The
second is a claim about the shoot that the data does not support.

## What running the loop found: not every alert is a disruption

The first end-to-end run failed in a way worth keeping. The location-hopping alert fired, the
sentinel read it over MCP, the orchestrator routed it to the replanner — and the replanner
came back with, in effect, *there is nothing I can do*:

> The replanning tool indicates that this disruption does not affect any scheduled scenes, so
> there is nothing to replan. Perhaps the alert points to an inefficiency in the current
> schedule rather than a disruption that needs to be absorbed.

That is correct, and it is a design gap rather than a bug. `ProposeAsync` models a
**disruption**: something blocks scenes on dates, and the solver re-solves around it. "One day
visits four locations" blocks nothing. It cancels no permit and makes no actor unavailable, so
there were no scene-dates to forbid, and `ReplanService` rightly refused rather than
manufacturing an answer.

(The model also invented a start date — 2024-04-23, from nowhere — because the alert carried
none and it had no clock. The demo passes today's date in now. A model asked for a date it was
never given will produce one.)

### Two labels, because *what happened* and *what to do* are different questions

`stripboardTrigger` says what happened. `stripboardAction` says what to do:

- **`replan`** — something blocked scenes; re-solve around them.
- **`consolidate`** — nothing is blocked and the plan itself is poor; re-solve under a tighter
  constraint and report what obeying it costs.

`ProposeConsolidationAsync` and `POST /api/schedule/consolidate` implement the second, backed
by a new hard constraint in CP-SAT:

```
sum over locations of atLocation[l, d]  <=  MaxLocationsPerDay      for every day d
```

The objective already *priced* company moves, but pricing is not forbidding — a solver saving
a whole shooting day will happily buy four locations to do it. Making the cap hard, and leaving
the day count free to rise, turns an argument into a number: **this costs you N shooting days
and $X**. The producer still decides.

Both options come back, including the schedule as it stands, so the choice is a comparison
rather than a recommendation with nothing to compare against:

```
Leave it — the worst day visits 4 locations    3 days   6 moves   $36,600
Consolidate — at most 2 locations a day        5 days   4 moves   $42,200   (+2 days, +$5,600)
```

### A third bug, found by the number that would not move

The first version of this reported the *same* company-move count on both sides, which made
consolidating look like pure loss. The metric was wrong, not the feature.

`ScheduleService` counted every location change in shooting order. Two things were wrong with
that, and both made the figure disagree with the solver that produced the schedule:

1. **It counted the overnight relocation** — the change from the last location of one day to
   the first of the next. That happens between wrap and call. It costs no shooting time, the
   solver does not charge for it, and it dominated the total, so capping a day removed
   nothing from a number made mostly of travel a cap cannot touch.
2. **It counted every transition in script order.** A day holding scenes 4, 6 and 13 with
   scene 6 elsewhere was charged two moves for an A→B→A journey no unit would make. A day is
   shot by finishing everywhere before the trucks move, and never coming back.

Both are now one rule, and it is the solver's rule: visiting *n* locations in a day costs
*n − 1* moves. The board also orders each day's scenes by location, so the call sheet shows
the shooting order the count assumes rather than a script order that implies driving in
circles.

The general shape is familiar by now. A figure derived twice, by two components, from two
different definitions, will disagree — and the one on screen is the one that gets believed.


## The bug this found: every metric read zero

With the rules live, all four sat in `normal`. `shoot_union_violations` was 0,
`shoot_days_total` was 0, `shoot_cost_estimate_usd` was 0 — on a service that was serving a
committed two-day schedule at the time.

Two causes, both worth writing down.

**A gauge with nothing to report was reporting 0.** `ShootMetrics.Read` returned
`_board is null ? 0 : …`. So before anything was scheduled the wire carried
`shoot_union_violations 0` and `shoot_days_total 0` — which reads as a clean, short shoot.
The union-violation rule saw zero violations and stayed green, which is the worst possible
failure for an alert: silence that looks like health. Observable gauges may publish no
measurement at all, so now they publish none, and `noDataState` handles the rest.

This is the same fault the project has now corrected in four places — infeasible replan
options returning zeros, the fabricated audit events, the hardcoded proposals, and now this.
Zero is a measurement. "I don't know" is not zero.

**The metrics restarted empty and were never refilled.** Startup solves an initial schedule
only when the database has none. After EV-22 put the schedule in Cloud SQL, that branch
started returning early on every restart — correctly, there was nothing to solve — and the
in-memory metrics were never told about the schedule that already existed. So a redeploy
silently blanked every `shoot_*` series until someone happened to commit a new version.
Startup now reads the active board and publishes it.

## A second, quieter bug

Alert summaries came back through MCP with `—` rendered as `â€"`. The MCP client read the
SSE body with `response.text`, and `requests` decodes a `text/*` body with no charset as
ISO-8859-1. MCP is UTF-8 by specification. The JSON still parsed, so nothing failed — it just
corrupted every non-ASCII character in every tool result, which would have reached a producer
in an answer about their own shoot.

This is the second encoding fault in the project with the same shape as the one in
[ADR-017](ADR-017-adk-replanner.md): a default that guesses an encoding, and text that is
wrong rather than absent.

## Two deployment faults this work exposed

Neither is about alerting, and both had the same shape as everything else here: a success that
was not one.

**`deploy-sentinel.sh` deployed nothing and said it had.** The service spec referenced
`sentinel:latest`, and `gcloud run services replace` diffs the spec. Rebuilding the image moved
the tag but left the spec byte-identical, so Cloud Run created no revision and carried on
serving the old container while the script printed "Sentinel deployed". The spec now pins the
image digest, so it differs exactly when the image differs, and the script reports the
revision number it actually produced — or says plainly that nothing changed.

**Stopping the database would have crash-looped the web app.** Migration ran before
`app.Run()` and threw on an unreachable Postgres, so the container never started; with
`min-instances=0` every cold start would have failed and the public URL would have answered 503
with nothing to explain it. Since stopping Cloud SQL between demos is a deliberate,
cost-driven act, it has to degrade the app rather than break it. Startup now records the
failure, `/api/health` returns 503 naming the database as the reason, and the log carries the
command that restarts it.

## Consequences

- Grafana is now a participant, not a destination.
- A stack without the rules provisioned makes the integration test fail rather than skip.
- Thresholds are conservative on purpose. An alert that fires constantly is one a 1st AD
  learns to ignore.
- Creating the first rule in a brand-new folder answers 403 for a few seconds while Grafana
  Cloud resolves the folder-scoped grants. The provisioner waits, because the alternative is
  telling an operator their token is wrong when it is fine.
