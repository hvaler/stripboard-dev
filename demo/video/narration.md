# Narration — Stripboard, 3 minutes

Written to be read by a synthetic voice, so it avoids what trips one up: no parentheses, no
em-dashes mid-clause, no numbers a voice would read wrongly. Every figure is spelled the way it
should be said.

**Pace: 150 words per minute.** Word counts per shot are given so a shot that overruns can be
cut before recording rather than after. Total 418 words, which lands near 2:47 and leaves
thirteen seconds of air — deliberately, because a video that runs to 3:01 is a video the judges
stop watching at three minutes.

**Recording order that works**: capture the screen silently first, generate the voice second,
align third. That way a slow page costs you a trim, not a retake.

---

## 0:00 – 0:25 · Mission Control (60 words)

> This is not our application's latency. It is a film shoot.
>
> Days remaining. Budget burning down. Union violations. And the one that costs real money:
> which actors we are paying to sit in a trailer.
>
> Every other project pointing Grafana at something this week is pointing it at itself. We
> pointed it at the production.

## 0:25 – 0:50 · Screenplay to stripboard (58 words)

> A screenplay goes in. Gemini, on Vertex AI, reads it into typed scenes: location, set,
> interior or exterior, day or night, cast, page count.
>
> Then a constraint solver builds the schedule. Google OR-Tools, CP-SAT. Twelve hour turnaround
> holds by construction, not by checking afterwards and apologising.

## 0:50 – 1:30 · Grafana starts the loop (96 words)

> Now watch what starts this.
>
> The shoot exports its own metrics to Grafana Cloud. A rule evaluates them, and it is firing:
> three cast members are called on a quarter of the shooting days. That is a contract being paid
> against days nobody works.
>
> Nobody is watching a screen. The Conflict Sentinel asks Grafana which rules are firing, through
> the official Grafana MCP server, and reads that alert back.
>
> This is the direction that matters. Grafana does not receive the result. Grafana starts the
> work.

## 1:30 – 2:15 · Agents and the solver (104 words)

> The orchestrator routes it. A root agent with no tools of its own, so nothing it reports can
> be a figure it invented.
>
> The replanner does no arithmetic at all. Every number it states comes from a separate CP-SAT
> run. Two options, with real cost deltas between two solved schedules.
>
> And when the two options come out identical, it says so, instead of dressing one up as a
> recommendation. Extending the window only permits extra days. The solver still minimises them.
>
> The honest answer was: you do not need the extra day.

## 2:15 – 2:45 · The refusal (63 words)

> Now the agent tries to commit its own recommendation.
>
> Refused.
>
> Not because we withheld the tool. The governance agent has the commit tool, is told to use it,
> and the scheduling service turns it down. The check lives behind an HTTP boundary where no
> prompt can argue with it.
>
> An identity in a request body is a claim. A claim cannot commit.

## 2:45 – 3:00 · The human, and back to Grafana (37 words)

> The Producer approves. New version. New audit entry, naming who proposed it and who approved
> it.
>
> And the decision is written back to Grafana as an annotation, so the timeline shows why the
> schedule changed.
