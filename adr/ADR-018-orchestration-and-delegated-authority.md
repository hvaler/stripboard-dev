# ADR-018 — Orchestration by delegation, and where authority lives

**Status:** Accepted · 2026-08-05 · Implements EV-25 · Builds on [ADR-017](ADR-017-adk-replanner.md)

## Context

The replanner (ADR-017) answers one kind of question well and nothing else. A producer asking
"where are we?" gets a replan they did not want, and asking "commit version 4e43f904" gets a
tool call it has no business making. The project claimed a multi-agent system; it had one
agent with one tool.

The second problem is more important. `AgentAuthorizationService` has always said that only a
Producer may commit a schedule, and until now nothing exercised it from the outside. A rule
no caller has ever hit is a rule nobody has tested.

## Decision

### A root agent with no tools

`line_producer` routes and does nothing else. It owns no tools at all, so it cannot read a
schedule, cost an option or commit anything. Its entire job is to hand the request to
`scheduler`, `replanner` or `governance` through ADK's sub-agent transfer.

This is not tidiness. A root agent that both routes *and* answers will sometimes answer from
memory — the model has the conversation in front of it and a plausible number is cheap. With
no tools there is nothing to answer from. If a specialist did not report a figure, no figure
exists to state.

The specialists get exactly one tool each:

| Agent | Tool | Backed by |
|---|---|---|
| `scheduler` | `get_schedule` | `GET /api/schedule` |
| `replanner` | `propose_replan` | `POST /api/replan` → CP-SAT |
| `governance` | `commit_schedule` | `POST /api/schedule/commit` |

`propose_replan` is imported from `agents/replanner`, not reimplemented. Two copies of a tool
drift, and the copy that drifts is the one nobody watches.

### The commit is refused, not withheld

The obvious way to keep an agent from committing is to not give it the tool. That proves
nothing, and a judge is right to be unimpressed: an agent cannot violate a rule it was never
in a position to test.

So `governance` *has* the commit tool, and is instructed to use it. When it does, the service
returns **403** and the reason. The agent reports the refusal, and its instruction forbids
retrying under a different identity.

The authority check lives in `ScheduleService.CommitAsync`, behind the HTTP boundary, in .NET
— somewhere no prompt can reach. This is the difference between a system that is safe and a
system that has been asked nicely.

```
> Commit schedule version 4e43f904-…. My identity is sa-stripboard-replanner.
  handled by: governance
  tool:       governance -> commit_schedule({version_id: '4e43f904-…',
                                             identity: 'sa-stripboard-replanner'})

  The commit was refused because 'sa-stripboard-replanner' cannot commit a schedule.
  Only the Producer role may commit. Agents can propose, but humans decide.
```

### Two endpoints the orchestrator needed

`GET /api/schedule` returns the committed board — days, units, company moves, union
violations, cost, and the day-by-day plan. When no schedule exists it returns 404 with a
sentence, not an empty board: zero days reads as a measurement, and "nothing has been
scheduled" is a different answer from "a shoot lasting no days".

`POST /api/schedule/commit` is the governance path above.

## A defect this surfaced

Running the chain end to end showed the replanner offering two options with identical figures:
3 days, $40,100, 8 moves, 0 violations, twice. The model dutifully recommended one "because it
is the most cost-effective", which was meaningless — they cost the same.

The cause is in the strategies, not the model. "Extend the schedule" *permits* extra days; it
does not require them. The solver still minimises days, so any disruption it can absorb yields
the same answer under both. That is a genuinely useful finding — *you do not need the extra
day* — presented as a choice between two things that are the same.

`ReplanService.MarkDuplicates` now flags an option whose metrics match an earlier one, and the
field is called `SameFiguresAs` rather than `SameSchedule`: the scene ordering may well differ,
and only the decision-relevant figures were compared. Saying more than that would be a smaller
version of the same fault.

Both the API and the Proposals page carry the flag, and the agent instructions say to report
such an option as the same outcome reached another way.

## Consequences

- The orchestrator needs the scheduling service; unreachable is an error it reports, never a
  plan it invents.
- Delegation costs one extra model turn per request (the transfer). Acceptable: the
  alternative is one agent whose instruction tries to cover three jobs.
- The sentinel is deliberately still not an ADK agent — its loop dispatches tools discovered
  from MCP at runtime, for the reason in [ADR-010](ADR-010-grafana-mcp-sidecar-transport.md).
- Ten tests cover the tools and the tree. They run offline against a fake service, so the
  suite does not depend on a Gemini quota; the end-to-end run is `demo/run_orchestrator.py`.
