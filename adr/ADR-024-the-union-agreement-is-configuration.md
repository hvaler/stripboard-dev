# ADR-024 — The union agreement is configuration, not physics

**Status:** Accepted · 2026-08-07 · Implements EV-42 · Refines [ADR-012](ADR-012-scheduling-model.md)

## Context

Three numbers ran this schedule from the first commit: twelve hours of turnaround, a meal break
inside six hours of continuous work, fourteen hours coming off a night. They are IATSE and
SAG-AFTRA figures, and `UnionRulesService` said so — in an XML doc comment, which is to say
nowhere a reader would find it.

An external review claimed the repository never named the agreement. That is not quite right:
it named it once, in a place only somebody reading the class would see. The real problem is the
one underneath. **The demo screenplay is set in Salford and Manchester**, and a British 1st AD
reading "12-hour turnaround" over a Salford Quays location notices immediately, because on this
side of the Atlantic the figure is eleven. Hardcoding one union's numbers makes a tool for one
union's territory and says nothing about it.

## Decision

`UnionAgreement` is a domain record carrying the three thresholds, with two profiles:
`IatseSagAftra` and `EuropeanDailyRest`. `UnionRulesService` reads them instead of constants,
`ScheduleService` takes one and passes it to the solver, and the profile is selected by
configuration (`Stripboard:UnionAgreement`).

### The longest lawful day is derived, not configured

The one decision worth arguing about. `MaxHoursPerDay` is **not** a fourth setting:

```csharp
public int MaxHoursPerDay => (int)(TimeSpan.FromHours(24) - MinimumTurnaround).TotalHours;
```

A day that ran longer than twenty-four hours minus the rest owed before the next call would
leave less than that rest, and the schedule would be illegal by arithmetic. Exposing it as its
own knob would allow a configuration that contradicts itself — a thirteen-hour day under a
twelve-hour agreement — and the solver would faithfully build an unlawful schedule that the
validator would then complain about. Deriving it is what stops those two disagreeing.

It also has the useful property of making the profile **matter**: eleven hours of rest permits
a thirteen-hour day, a thirteen-hour day fits more of the screenplay, and the same script needs
fewer of them. Changing the agreement changes the schedule, not merely the warnings. There is a
test that fails if the two profiles ever produce the same day count.

### The European profile is not claimed to be anybody's contract

It is modelled on the eleven consecutive hours of daily rest in the Working Time Directive, and
labelled as that rather than as BECTU or PACT. Naming a specific union's agreement for numbers
nobody has checked against it would be false precision of exactly the kind this codebase spends
its time removing. A production adopting this sets its own figures from its own agreement; what
the second profile proves is that it *can*.

### An unknown name throws

`FromName("bectu-2019")` raises at startup rather than falling back to the American figures.
A silent default would schedule a European shoot to twelve-hour rest periods and then report
no violations, because the validator would be reading the same wrong agreement — wrong and
self-consistent, which is the hardest kind of wrong to notice.

## Consequences

- The three thresholds appear once, in `UnionAgreement`, and reach both the validator and the
  solver from `ScheduleService`. Two copies is how a board ends up warning about a rule the
  schedule was never built to satisfy.
- `UnionRulesService.MinimumTurnaroundHours` stays as the IATSE figure so existing callers and
  the mutation-tested suite (ADR-022) keep meaning what they meant.
- The default is unchanged, so every figure published before today still stands.
- What this does **not** do is model any agreement in full. Turnaround, meal breaks and the
  night-to-day transition are three rules out of a contract that runs to hundreds of pages;
  the rest are still not represented, and pretending otherwise would be worse than the
  hardcoding this replaces.
