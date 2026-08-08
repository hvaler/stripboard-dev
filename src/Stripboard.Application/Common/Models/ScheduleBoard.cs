using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;

namespace Stripboard.Application.Common.Models;

/// <summary>
/// Read models for the stripboard UI (EV-21).
///
/// These exist so no view has to reach into the domain or invent data: every figure a page
/// renders is produced here from a persisted <see cref="ScheduleVersion"/> and a real solver
/// run. Nothing in this file is a placeholder.
/// </summary>
public record BoardScene(
    int Number,
    string SetLocation,
    IntExt IntExt,
    DayNight DayNight,
    int Eighths,
    string Synopsis,
    IReadOnlyList<string> Cast
);

public record BoardDay(
    int DayNumber,
    DateOnly Date,
    string LocationName,
    TimeOnly CallTime,
    TimeOnly WrapTime,
    IReadOnlyList<BoardScene> Scenes,
    bool IsCompanyMove,
    double TurnaroundHoursFromPreviousDay,
    IReadOnlyList<string> Locations
);

public record ScheduleMetrics(
    int TotalDays,
    int CompanyMoves,
    int TotalEighths,
    decimal EstimatedCostUsd,
    bool IsOptimal,
    int UnionViolations
);

/// <summary>
/// A schedule as anyone reading it should see it.
///
/// <see cref="CreatedBy"/> and <see cref="ApprovedBy"/> are two different people and are kept
/// apart on purpose. The board is *proposed* by whoever ran the solver — usually an agent — and
/// *approved* by a human Producer, which is the only identity the service lets commit. They used
/// to be one field, and the screen consequently read "Committed · created by sa-replanner": a
/// service account presented as the approver of a rule that exists to keep service accounts out.
/// </summary>
public record ScheduleBoard(
    Guid VersionId,
    int VersionNumber,
    bool IsCommitted,
    string CreatedBy,
    DateTime CreatedAt,
    IReadOnlyList<BoardDay> Days,
    IReadOnlyList<Anomaly> Anomalies,
    ScheduleMetrics Metrics,
    string SolverMessage,
    // Null on a draft, and also on versions committed before the two were separated. Absent is
    // the honest answer there: filling it in from CreatedBy would recreate the very claim this
    // change exists to remove.
    string? ApprovedBy = null,
    DateTime? ApprovedAt = null
);

/// <summary>What a disruption costs relative to the schedule it disrupts.</summary>
public record CostDelta(
    int ExtraShootDays,
    int ExtraCompanyMoves,
    int ExtraUnionViolations,
    decimal CostDeltaUsd
);

public record ReplanOption(
    Guid VersionId,
    string Title,
    string Strategy,
    string Justification,
    ScheduleMetrics Metrics,
    CostDelta Delta,
    bool IsFeasible,
    /// <summary>
    /// Set when this option matched an earlier one on every figure a producer decides with.
    /// The two schedules may still order scenes differently, but there is no trade-off left
    /// to weigh, and presenting them as a choice implies one that does not exist.
    /// </summary>
    string? SameFiguresAs = null,
    /// <summary>
    /// True when this option *is* the schedule currently in force.
    ///
    /// Without it the page offered an already-approved option as though it were pending, with
    /// deltas promising a saving the producer had already banked. Approving changed nothing
    /// visible, so it read as a broken button — and the natural response is to press it again.
    /// An option that is already the plan is not a decision; saying so is.
    /// </summary>
    bool IsCommitted = false
);

/// <summary>A disruption as submitted by an operator or a watcher agent.</summary>
public record DisruptionRequest(
    TriggerType TriggerType,
    DateOnly StartDate,
    int DurationDays,
    Guid? PersonId,
    string? LocationName,
    string Description
);

/// <summary>
/// The physical shape of a shooting day, shared by the solver (which must reason about it
/// to build a legal schedule) and by anything that reads a schedule back (which must
/// describe the same day the same way). Duplicating these numbers is how a board ends up
/// disagreeing with the model that produced it.
/// </summary>
public static class ShootDayModel
{
    /// <summary>Meal break reserved once the day runs past <see cref="MinutesBeforeMealBreak"/>.</summary>
    public const int MealBreakMinutes = 60;

    /// <summary>Union limit on continuous work without a meal break.</summary>
    public const int MinutesBeforeMealBreak = 6 * 60;

    /// <summary>Time the unit loses to a single company move.</summary>
    public const int CompanyMoveMinutes = 60;

    /// <summary>
    /// Longest unbroken stretch of work in a day. With one meal break the work splits in
    /// two, so this is what the union meal rule should be asked about — not the total,
    /// which would report a missing break the schedule actually reserves.
    /// </summary>
    public static int LongestContinuousStretch(int workMinutes) =>
        workMinutes > MinutesBeforeMealBreak ? (workMinutes + 1) / 2 : workMinutes;

    /// <summary>Call-to-wrap length of a day, including its meal break and company moves.</summary>
    public static int ElapsedMinutes(int workMinutes, int companyMoves) =>
        workMinutes
        + (workMinutes > MinutesBeforeMealBreak ? MealBreakMinutes : 0)
        + Math.Max(0, companyMoves) * CompanyMoveMinutes;
}

/// <summary>
/// Production cost model. Crude but honest: every figure the UI shows is derived from these
/// rules and the cast/crew day rates in the database, never from a literal in a page.
///
/// **Where the numbers come from (EV-44).** A cost model nobody can check is a number with a
/// dollar sign in front of it, so each figure below says what it is anchored to and which of
/// them is a stand-in.
///
/// The day rates live in the database, not here, and the seeded ones are close to scale: a
/// SAG-AFTRA day player on the 2025–26 Basic Theatrical Agreement is **$1,246 in wages plus
/// $261.66 pension and health — $1,507.66 before overtime or overscale**, against the $1,500
/// the demo seeds for a lead. Low-budget agreements run at 65% of that and micro-budget at
/// 35%, which is why a production substitutes its own rates rather than adopting these.
///
///   https://www.topsheet.io/edu/rates/sag-aftra/sag-aftra-theatrical-rates-2025
///   https://www.wrapbook.com/blog/essential-guide-sag-rates
/// </summary>
public static class CostModel
{
    /// <summary>
    /// Moving the whole unit to another location costs a fixed sum.
    ///
    /// **This one is a stand-in and should be read as one.** There is no published rate for a
    /// company move; what it really costs is transport plus the shooting hour it eats, and the
    /// hour is the larger half — <see cref="ShootDayModel.CompanyMoveMinutes"/> already charges
    /// the schedule for it, so this figure is the cash on top. $2,500 is the order of magnitude
    /// a mid-size unit spends moving trucks, crew and equipment across a city, not a quotation.
    /// </summary>
    public const decimal CompanyMoveUsd = 2_500m;

    /// <summary>
    /// A union violation carries a penalty payment.
    ///
    /// **Blended on purpose, and the blend is worth knowing.** The two violations this model
    /// detects cost very different amounts in reality:
    ///
    /// - A **meal penalty** is per performer and escalates: $25 for the first half hour, $35
    ///   for the second, $50 for each thereafter. On a six-person cast an hour late that is
    ///   $360, not $750.
    /// - A **turnaround violation** is the expensive one. Cutting a performer's rest short is
    ///   compensated at up to a full day's pay, so a single one costs more like $1,246 —
    ///   nearly twice this figure.
    ///
    /// One constant cannot be both, and the honest reading of $750 is a midpoint that keeps
    /// violations expensive enough to matter in the objective without pretending to price a
    /// specific breach. Pricing them separately is the obvious next refinement; it needs the
    /// anomaly type to reach the cost model, which today it does not.
    ///
    ///   https://www.sagaftra.org/meal-periods
    ///   https://www.wrapbook.com/blog/meal-penalties-producers-guide
    /// </summary>
    public const decimal UnionViolationPenaltyUsd = 750m;

    /// <summary>Cost of one shooting day: every crew member, plus the cast actually called.</summary>
    public static decimal DayCost(IEnumerable<Person> crew, IEnumerable<Person> castCalled) =>
        crew.Sum(p => p.DailyRate) + castCalled.Sum(p => p.DailyRate);
}
