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

public record ScheduleBoard(
    Guid VersionId,
    int VersionNumber,
    bool IsCommitted,
    string CreatedBy,
    DateTime CreatedAt,
    IReadOnlyList<BoardDay> Days,
    IReadOnlyList<Anomaly> Anomalies,
    ScheduleMetrics Metrics,
    string SolverMessage
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
    string? SameFiguresAs = null
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
/// </summary>
public static class CostModel
{
    /// <summary>Moving the whole unit to another location costs a fixed sum.</summary>
    public const decimal CompanyMoveUsd = 2_500m;

    /// <summary>A union violation carries a penalty payment.</summary>
    public const decimal UnionViolationPenaltyUsd = 750m;

    /// <summary>Cost of one shooting day: every crew member, plus the cast actually called.</summary>
    public static decimal DayCost(IEnumerable<Person> crew, IEnumerable<Person> castCalled) =>
        crew.Sum(p => p.DailyRate) + castCalled.Sum(p => p.DailyRate);
}
