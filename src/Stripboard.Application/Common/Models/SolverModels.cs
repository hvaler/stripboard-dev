using Stripboard.Domain.Entities;

namespace Stripboard.Application.Common.Models;

public record LocationPermitWindow(
    string LocationName,
    DateOnly StartDate,
    DateOnly EndDate
);

/// <summary>
/// Forbids a specific scene from being shot on a specific date. This is how a disruption
/// reaches the solver: an unavailable actor, an expired permit or a washed-out exterior all
/// reduce to "these scenes cannot happen on these days" (EV-21).
/// </summary>
public record BlockedSceneDate(int SceneNumber, DateOnly Date, string Reason);

public record SolverInput(
    List<Scene> Scenes,
    List<Person> CastAndCrew,
    List<LocationPermitWindow> PermitWindows,
    DateOnly ScheduleStartDate,
    int MaxDaysAvailable = 10,
    int MaxHoursPerDay = 12,
    double MinimumTurnaroundHours = 12.0,
    List<BlockedSceneDate>? BlockedSceneDates = null
);

public record ScheduledDayResult(
    int DayNumber,
    DateOnly Date,
    string LocationName,
    TimeOnly CallTime,
    TimeOnly WrapTime,
    List<Scene> ScheduledScenes
);

public record SolverOutput(
    bool IsOptimal,
    bool IsFeasible,
    long ObjectiveValue,
    List<ScheduledDayResult> ScheduledDays,
    List<Anomaly> DetectedAnomalies,
    string SolverMessage
);
