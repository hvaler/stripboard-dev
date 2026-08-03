using Stripboard.Domain.Entities;

namespace Stripboard.Application.Common.Models;

public record LocationPermitWindow(
    string LocationName,
    DateOnly StartDate,
    DateOnly EndDate
);

public record SolverInput(
    List<Scene> Scenes,
    List<Person> CastAndCrew,
    List<LocationPermitWindow> PermitWindows,
    DateOnly ScheduleStartDate,
    int MaxDaysAvailable = 10,
    int MaxHoursPerDay = 12,
    double MinimumTurnaroundHours = 12.0
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
