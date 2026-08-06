# ADR-022 — Mutation testing, where the claim is worth making

**Status:** Accepted · 2026-08-05 · Makes a claim the README carried for weeks true

## Context

The README said *"union rules verified with mutation testing"*. No such configuration
existed. EV-17 removed the sentence rather than leaving it
standing, which was right at the time.

The claim was worth making, though, and for a specific reason. `UnionRulesService` decides
whether a schedule is legal. A test suite that still passes after the turnaround check is
mutated is a suite that was never testing the turnaround check — and unlike most code, here
that means a shoot that breaks the law with the tests green.

## Decision

Stryker.NET over `Stripboard.Domain`, scoped to `Services/UnionRulesService.cs`, break
threshold 85%.

### Why scoped, and why that is not score-gaming

The first run covered the whole domain layer and scored **43.12%**, well under the threshold.
Most of the survivors were in entity constructors and property assignments: mutating
`Disruption`'s constructor measures nothing anybody wants to know.

There were two honest responses. Lower the threshold to what the code scores — which is
threshold-fitting, the same fault as calibrating an alert to fire on today's number. Or point
the tool at the code where the claim means something and hold that to a high bar.

The scope is `UnionRulesService` because that is the file the sentence was ever about.

### What it found

Seven mutants survived the existing suite. Two were real gaps:

**`isPreviousNight && isCurrentDay` → `||` survived.** Nothing tested that the circadian rule
applies *only* to a night followed by a day. Under the mutant, an ordinary day-to-day
transition with 13 hours of rest would raise a night-transition anomaly — a false alarm a 1st
AD would learn to ignore, and with it the rest of the alerts.

**`restDuration < 14h` → `<= 14h` survived.** Exactly fourteen hours was untested, so the two
were indistinguishable. The threshold *is* the rule; an unpinned boundary is an unpinned rule.

The other five were deleted `ArgumentNullException.ThrowIfNull` guards. Minor, but a removed
guard turns a clear exception into a `NullReferenceException` from somewhere deeper, which is
a much worse thing to debug at five in the morning.

### The test that did not kill what it was written for

Two guards stayed alive after the first fix, and the reason is the interesting part. The new
test called `ValidateNightDayTransition(null, day, true, true)` — and with the guards deleted
it *still* threw, because `ValidateTurnaround` guards its own arguments. The test passed
either way and proved nothing.

With the flags off, the method returns before touching either day, so a deleted guard turns a
programming error into a silent `null`. Calling it with `false, false` is what kills the
mutant. A null day is a bug whether or not the rule ends up applying, and the method has to
say so at the top.

**Final score: 100%, 21 mutants killed, 0 survived.**

## Consequences

- The README claim is true, and narrower than it was: mutation testing covers the union
  rules, not the codebase.
- `dotnet stryker` is a manual step, not CI. A full run takes minutes and the domain layer
  changes rarely; wiring it into every push would buy little and cost every contributor time.
- The 43% figure for the wider domain layer is recorded here rather than hidden. It is not
  alarming — those are constructors — but it is the number, and the next person to widen the
  scope should know what they are walking into.
- Scope is a judgement, and judgements drift. If `UnionRulesService` ever grows a dependency,
  or the rules move somewhere else, this configuration is pointing at the wrong file and will
  keep reporting 100% while measuring nothing.
