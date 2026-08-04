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
    bool IsFeasible
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
