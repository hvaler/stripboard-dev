# Narration — Stripboard, 3 minutes

Written to be read by a synthetic voice, so it avoids what trips one up: no parentheses, no
em-dashes mid-clause, no numbers a voice would read wrongly. Every figure is spelled the way it
should be said.

**This file is the source.** `make_voiceover.py` reads it to synthesise the audio and to write
`subtitles.srt`, so the words spoken, the words captioned and the words here cannot drift apart.
They used to live in three places with a comment asking you to remember all of them.

**The headings are measured, not planned.** Each one is where that shot's voice actually starts
and ends, read back from the generated audio with a four-second gap between shots. Change a
sentence and the script tells you which window it broke, before you record rather than after.

**Total: 2:43**, seventeen seconds inside the limit. That margin is deliberate — a video that
runs to 3:01 is a video the judges stop watching at three minutes — and it is real margin: the
figure came from `ffprobe` on the files themselves, not from words-per-minute arithmetic. The
arithmetic was wrong by a factor of two for a week and made this narration look like it ran to
four and a half minutes.

**Recording order that works**: capture the screen silently first, generate the voice second,
align third. That way a slow page costs you a trim, not a retake.

---

## 0:00 – 0:19 · Mission Control

> This is not our application's latency. It is a film shoot.
>
> Days remaining. Budget burning down. Union violations. And the one that costs real money:
> which actors we are paying to sit in a trailer.
>
> Every other project pointing Grafana at something this week is pointing it at itself. We
> pointed it at the production.

## 0:22 – 0:38 · Ask your shoot

> A line producer does not read dashboards. They ask questions.
>
> So ask one, in plain English. Gemini answers by querying Grafana live, over the Model Context
> Protocol, and shows the query it ran.
>
> Twenty six thousand eight hundred dollars. Read off a metric, not guessed at.

## 0:42 – 1:01 · Screenplay to stripboard

> A screenplay goes in. Gemini, on Vertex AI, reads it into typed scenes: location, set,
> interior or exterior, day or night, cast, page count.
>
> Then a constraint solver builds the schedule. Google OR-Tools, CP-SAT. Twelve hour turnaround
> holds by construction, not by checking afterwards and apologising.

## 1:04 – 1:33 · Grafana starts the loop

> Now watch what starts this.
>
> The shoot exports its own metrics to Grafana Cloud. A rule evaluates them, and it is firing:
> one shooting day visits four locations. Each company move costs an hour of shooting light.
> A day in the van.
>
> Nobody is watching a screen. The Conflict Sentinel asks Grafana which rules are firing, through
> the official Grafana MCP server, and reads that alert back.
>
> This is the direction that matters. Grafana does not receive the result. Grafana starts the
> work.

## 1:37 – 2:03 · Agents and the solver

> The orchestrator routes it. A root agent with no tools of its own, so nothing it reports can
> be a figure it invented.
>
> The replanner does no arithmetic at all. Every number it states comes from a separate CP-SAT
> run. Two options, with real cost deltas between two solved schedules.
>
> And when both come out identical, it says so, instead of dressing one up as a recommendation.
> The honest answer was: you do not need the extra day.

## 2:07 – 2:28 · The refusal

> Now the agent tries to commit its own recommendation.
>
> Refused.
>
> Not because we withheld the tool. The governance agent has the commit tool, is told to use it,
> and the scheduling service turns it down. The check lives behind an HTTP boundary where no
> prompt can argue with it.
>
> An identity in a request body is a claim. A claim cannot commit.

## 2:31 – 2:44 · The human, and the trail

> The Producer approves. New version. New audit entry, naming who proposed it and who approved
> it.
>
> Sentinel raised it, an agent proposed it, a human decided. The trail records all three, and
> nothing rewrites it.
