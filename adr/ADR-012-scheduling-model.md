# ADR-012 — What the scheduling model actually models

**Status:** Accepted · 2026-08-04 · Implements EV-27 · Refines [ADR-002](ADR-002-solver.md)

## Context

The solver assigned scenes to days subject to a per-day capacity, and nothing else. The
schedules it produced were feasible arithmetic and unshootable film: a single day mixing a
breakfast scene with a night exterior, seven company moves inside one call, a twelve-hour
day with no meal break, and union rules "enforced" by inspecting the answer afterwards and
reporting violations rather than preventing them.

The class doc claimed it "enforces 12h union turnaround as a hard constraint". It did not.

## Decision

### What the model decides

Three families of variables: which day each scene is shot on, whether each day is a **day
unit** or a **night unit**, and which locations each day visits.

### Constraints

1. Every scene shot exactly once.
2. **Day length**, covering work + meal break + company moves, capped at `MaxHoursPerDay`.
3. Permit windows.
4. Scenario blocks from a disruption being replanned (EV-21).
5. **Day Out of Days** — a scene cannot be shot on a date when any of its cast is
   unavailable. `Person.UnavailableDates` is now part of the domain.
6. **Day/night units** — a day shoots day scenes or night scenes, never both.
7. **Circadian rest** — a night unit may not be followed immediately by a day unit.
8. Scenes bind their day to their location, so visiting a location costs something.

### Turnaround is now a property, not a check

A day unit calls at 08:00, a night unit at 18:00, and constraint 2 caps elapsed time at
twelve hours. Given constraint 7, the worst case between wrap and the next call is exactly
twelve hours. `UnionRulesService` still runs over every result and is now *expected to find
nothing*; a turnaround anomaly means the model is wrong. That inversion is the point, and
there is a golden test asserting it.

### A company move costs time, not just objective points

This was the correction that mattered. Penalising moves in the objective did nothing,
because minimising shooting days legitimately outweighs it — crew day rate dwarfs a move.
The schedule still hopped seven locations in a day.

The missing fact was physical: **a company move consumes an hour of the shooting day.**
Charging it against the same twelve hours makes the seven-set day infeasible rather than
merely undesirable. Objective pressure alone cannot express "there are not enough hours".

### Objective weights are derived, not chosen

Priority is strict: days, then location-days, then earliness. The weights are computed from
the bounds of the terms below them:

```
maxEarliness      = numDays²
locationDayWeight = maxEarliness + 1
dayWeight         = locationDayWeight × (numLocations × numDays + 1)
```

A hand-picked `1000 / 100 / 10` looks like priority but silently inverts as soon as a
schedule grows past the scale those numbers assumed.

## Three bugs this uncovered

**The horizon was capped at the scene count.** `numDays = min(MaxDaysAvailable, numScenes)`
is a reasonable optimisation for pure packing and wrong the moment a date-based constraint
exists: a permit that opens next week, or an actor away for three days, needs calendar slots
further out than there are scenes. Two golden tests were infeasible until this was fixed.

**The meal rule was being asked the wrong question.** `ValidateMealPenalty` takes the longest
*continuous* stretch; the solver passed total work and `ScheduleService` passed call-to-wrap,
which counts the meal break itself as work. Every real day was therefore flagged as a
penalty risk. The day-shape constants and the stretch calculation now live in one place
(`ShootDayModel`) used by both the solver and the board that reads schedules back.

**Production day numbers were slot indices.** A schedule using slots 3 and 11 reported
"Day 11 of 2". Day numbers are now sequential over the days actually shot; the calendar
date is what skips.

## Result on the demo screenplay

| | Before | After |
|---|---|---|
| Shooting days | 2 | 3 |
| Union anomalies | 1 | **0** |
| Day/night mixing | yes | no — day 3 is a night unit, 18:00–02:45 |
| Meal break | none | reserved |
| Shortest turnaround | not modelled | 21.8h |

The schedule got longer and became legal. That is the correct direction.

## A limitation worth naming

Company moves stayed at 10, and that is optimal: the demo screenplay has **10 distinct
locations across 12 scenes**, and the sitting room is used both day and night, so eleven
location-days is the floor. There is nothing to group.

The deeper issue is that the model treats `SetLocation` as the location. In production a
*set* belongs to a *location*: moving from 221B's sitting room to 221B's laboratory is not
a company move, but moving from Covent Garden to Piccadilly is — even though both are
written as "LONDON STREETS - …". No string heuristic can separate those two cases, so
guessing would be worse than the current honest over-count.

The right fix is to extract location and set as distinct fields during breakdown, where
Gemini can actually tell them apart. That belongs to EV-28, and until it lands the company
move count is an upper bound rather than a true figure.
