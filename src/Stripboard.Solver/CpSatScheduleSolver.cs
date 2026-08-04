using Google.OrTools.Sat;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Common.Models;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Domain.Services;

namespace Stripboard.Solver;

/// <summary>
/// Deterministic film schedule solver on Google OR-Tools CP-SAT (§5 / ADR-002 / ADR-012).
///
/// What the model decides: which day each scene is shot on, whether each day is a day unit
/// or a night unit, and which locations each day visits. What it optimises, in strict
/// priority order: fewest shooting days, then fewest location-days (company moves), then
/// pack the shoot as early as possible.
///
/// Union turnaround is a property of the model rather than something checked afterwards:
/// a day unit calls at 08:00 and a night unit at 18:00, elapsed time is capped at
/// MaxHoursPerDay, and a night unit may not be followed by a day unit. Those three facts
/// together guarantee at least 12 hours between wrap and the next call. UnionRulesService
/// still runs over the result, and is expected to find nothing — it is a cross-check on the
/// model, not the enforcement mechanism.
/// </summary>
public class CpSatScheduleSolver : IScheduleSolver
{
    private readonly UnionRulesService _unionRulesService = new();

    // The physical shape of a day lives in ShootDayModel so the board that reads a schedule
    // back describes exactly the day this model built.
    private const int MealBreakMinutes = ShootDayModel.MealBreakMinutes;
    private const int MinutesBeforeMealBreak = ShootDayModel.MinutesBeforeMealBreak;

    /// <summary>
    /// Time the unit loses moving between locations. Charging for this is what stops the
    /// solver proposing a day that visits seven sets: penalising moves in the objective was
    /// not enough, because minimising days always outweighed it. A move costs hours, and a
    /// day only has twelve.
    /// </summary>
    private const int CompanyMoveMinutes = ShootDayModel.CompanyMoveMinutes;

    private static readonly TimeOnly DayUnitCall = new(8, 0);
    private static readonly TimeOnly NightUnitCall = new(18, 0);

    public Task<SolverOutput> SolveAsync(SolverInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Scenes.Count == 0)
        {
            return Task.FromResult(new SolverOutput(
                IsOptimal: true,
                IsFeasible: true,
                ObjectiveValue: 0,
                ScheduledDays: new List<ScheduledDayResult>(),
                DetectedAnomalies: new List<Anomaly>(),
                SolverMessage: "No scenes provided to solve."
            ));
        }

        var model = new CpModel();
        var scenes = input.Scenes;
        int numScenes = scenes.Count;

        // The horizon is whatever the caller allows, not min(days, scenes). Capping it at
        // the scene count assumes the only thing a day is good for is holding a scene, which
        // stops being true the moment a date-based constraint exists: a permit that opens
        // next week, or an actor away for three days, both need calendar slots further out
        // than there are scenes.
        int numDays = Math.Max(1, input.MaxDaysAvailable);

        // Company moves are measured against Location, not SetLocation: two rooms of the
        // same hotel are one place to park the trucks (EV-28).
        var locations = scenes
            .Select(s => s.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var locationIndex = locations
            .Select((name, i) => (name, i))
            .ToDictionary(t => t.name, t => t.i, StringComparer.OrdinalIgnoreCase);
        int numLocations = locations.Count;

        // --- variables ----------------------------------------------------------------

        var x = new BoolVar[numScenes, numDays];          // scene s is shot on day d
        for (int s = 0; s < numScenes; s++)
        {
            for (int d = 0; d < numDays; d++)
            {
                x[s, d] = model.NewBoolVar($"x_{s}_{d}");
            }
        }

        var used = new BoolVar[numDays];                  // day d is a shooting day
        var night = new BoolVar[numDays];                 // day d is a night unit
        for (int d = 0; d < numDays; d++)
        {
            used[d] = model.NewBoolVar($"used_{d}");
            night[d] = model.NewBoolVar($"night_{d}");
            model.Add(night[d] <= used[d]);               // only a shooting day can be a night
        }

        var atLocation = new BoolVar[numLocations, numDays];  // day d visits location l
        for (int l = 0; l < numLocations; l++)
        {
            for (int d = 0; d < numDays; d++)
            {
                atLocation[l, d] = model.NewBoolVar($"loc_{l}_{d}");
            }
        }

        // --- constraints --------------------------------------------------------------
        //
        // Note the ordering below is presentational; constraint 2 depends on the location
        // variables declared above, because a day's length includes its company moves.

        // 1. Every scene is shot exactly once.
        for (int s = 0; s < numScenes; s++)
        {
            var days = new List<BoolVar>();
            for (int d = 0; d < numDays; d++)
            {
                days.Add(x[s, d]);
            }
            model.Add(LinearExpr.Sum(days) == 1);
        }

        // 2. A day cannot run longer than MaxHoursPerDay from call to wrap. The meal break
        //    is reserved up front rather than modelled as a placement decision, which keeps
        //    the constraint linear and errs on the side of shorter days.
        int elapsedCapacity = input.MaxHoursPerDay * 60;
        int workCapacity = Math.Max(15, elapsedCapacity - MealBreakMinutes);
        for (int d = 0; d < numDays; d++)
        {
            var dayLoad = new List<LinearExpr>();
            for (int s = 0; s < numScenes; s++)
            {
                dayLoad.Add(x[s, d] * SceneMinutes(scenes[s]));
                model.Add(used[d] >= x[s, d]);
            }

            // Company moves eat into the same twelve hours. The number of moves on a day is
            // (locations visited − 1), which is zero when the day is not used at all.
            for (int l = 0; l < numLocations; l++)
            {
                dayLoad.Add(atLocation[l, d] * CompanyMoveMinutes);
            }
            dayLoad.Add(used[d] * -CompanyMoveMinutes);

            model.Add(LinearExpr.Sum(dayLoad) <= workCapacity);
        }

        // 3. Location permit windows.
        for (int s = 0; s < numScenes; s++)
        {
            var permit = input.PermitWindows.FirstOrDefault(p =>
                string.Equals(p.LocationName, scenes[s].Location, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.LocationName, scenes[s].SetLocation, StringComparison.OrdinalIgnoreCase));
            if (permit is null)
            {
                continue;
            }

            for (int d = 0; d < numDays; d++)
            {
                var date = input.ScheduleStartDate.AddDays(d);
                if (date < permit.StartDate || date > permit.EndDate)
                {
                    model.Add(x[s, d] == 0);
                }
            }
        }

        // 4. Scenario blocks from a disruption being replanned (EV-21).
        foreach (var block in input.BlockedSceneDates ?? new List<BlockedSceneDate>())
        {
            int s = scenes.FindIndex(sc => sc.Number == block.SceneNumber);
            int d = block.Date.DayNumber - input.ScheduleStartDate.DayNumber;
            if (s >= 0 && d >= 0 && d < numDays)
            {
                model.Add(x[s, d] == 0);
            }
        }

        // 5. Day Out of Days. A scene cannot be shot on a date when any of its cast is
        //    unavailable — the constraint a 1st AD treats as non-negotiable (EV-27).
        var unavailability = input.CastAndCrew
            .Where(p => p.UnavailableDates.Count > 0)
            .ToDictionary(p => p.Id, p => p.UnavailableDates.ToHashSet());

        if (unavailability.Count > 0)
        {
            for (int s = 0; s < numScenes; s++)
            {
                var cast = scenes[s].CastPersonIds;
                if (cast.Count == 0)
                {
                    continue;
                }

                for (int d = 0; d < numDays; d++)
                {
                    var date = input.ScheduleStartDate.AddDays(d);
                    bool blocked = cast.Any(id =>
                        unavailability.TryGetValue(id, out var dates) && dates.Contains(date));

                    if (blocked)
                    {
                        model.Add(x[s, d] == 0);
                    }
                }
            }
        }

        // 6. Day/night units. A day shoots either day scenes or night scenes, never both:
        //    the unit calls at one time, and mixing them is what produces impossible
        //    turnarounds in practice.
        for (int s = 0; s < numScenes; s++)
        {
            bool isNightScene = scenes[s].DayNight == DayNight.Night;
            for (int d = 0; d < numDays; d++)
            {
                if (isNightScene)
                {
                    model.Add(x[s, d] <= night[d]);
                }
                else
                {
                    model.Add(x[s, d] + night[d] <= 1);
                }
            }
        }

        // 7. Circadian rest: a night unit cannot be followed immediately by a day unit.
        //    Wrapping at 06:00 and calling at 08:00 is a 2-hour turnaround. With rule 2 and
        //    the fixed call times, forbidding this transition is what makes the 12-hour
        //    turnaround hold by construction.
        for (int d = 0; d < numDays - 1; d++)
        {
            model.Add(night[d] + used[d + 1] - night[d + 1] <= 1);
        }

        // 8. A scene binds its day to its location, so visiting a location costs something.
        for (int s = 0; s < numScenes; s++)
        {
            int l = locationIndex[scenes[s].Location];
            for (int d = 0; d < numDays; d++)
            {
                model.Add(x[s, d] <= atLocation[l, d]);
            }
        }

        // --- objective ----------------------------------------------------------------
        //
        // Strict priority: days first, then company moves, then earliness. The weights are
        // derived from the bounds of the terms below them so the ordering is guaranteed
        // rather than hoped for — a hand-picked 1000/100/10 breaks as soon as a schedule
        // grows past the arbitrary scale those numbers assumed.
        long maxEarliness = (long)numDays * numDays;
        long earlinessWeight = 1;
        long locationDayWeight = maxEarliness + 1;
        long maxLocationDays = (long)numLocations * numDays;
        long dayWeight = locationDayWeight * (maxLocationDays + 1);

        var objective = new List<LinearExpr>();
        for (int d = 0; d < numDays; d++)
        {
            objective.Add(used[d] * dayWeight);
            objective.Add(used[d] * (earlinessWeight * d));
            for (int l = 0; l < numLocations; l++)
            {
                objective.Add(atLocation[l, d] * locationDayWeight);
            }
        }
        model.Minimize(LinearExpr.Sum(objective));

        // --- solve --------------------------------------------------------------------

        var solver = new CpSolver { StringParameters = "max_time_in_seconds: 10.0" };
        var status = solver.Solve(model);

        bool isOptimal = status == CpSolverStatus.Optimal;
        bool isFeasible = isOptimal || status == CpSolverStatus.Feasible;

        if (!isFeasible)
        {
            return Task.FromResult(new SolverOutput(
                IsOptimal: false,
                IsFeasible: false,
                ObjectiveValue: -1,
                ScheduledDays: new List<ScheduledDayResult>(),
                DetectedAnomalies: new List<Anomaly>
                {
                    new(Guid.NewGuid(), AnomalySeverity.Critical, AnomalyType.CastUnavailable,
                        Explain(input, numDays)),
                },
                SolverMessage: $"No schedule satisfies these constraints (CP-SAT status: {status})."
            ));
        }

        return Task.FromResult(Extract(input, solver, x, used, night, numScenes, numDays, status, isOptimal));
    }

    private static int SceneMinutes(Scene scene) => Math.Max(15, scene.Eighths * 15);

    /// <summary>
    /// Turns an infeasible model into something a producer can act on, rather than
    /// "infeasible".
    /// </summary>
    private static string Explain(SolverInput input, int numDays)
    {
        var reasons = new List<string>();

        int totalMinutes = input.Scenes.Sum(SceneMinutes);
        int capacity = numDays * Math.Max(15, input.MaxHoursPerDay * 60 - MealBreakMinutes);
        if (totalMinutes > capacity)
        {
            reasons.Add($"the screenplay needs {totalMinutes / 60.0:0.#}h of shooting but only "
                      + $"{capacity / 60.0:0.#}h are available across {numDays} day(s)");
        }

        var blockedCast = input.CastAndCrew.Where(p => p.UnavailableDates.Count > 0).ToList();
        if (blockedCast.Count > 0)
        {
            reasons.Add("cast availability: "
                      + string.Join("; ", blockedCast.Select(p => $"{p.Name} unavailable on "
                      + string.Join(", ", p.UnavailableDates.Select(d => d.ToString("yyyy-MM-dd"))))));
        }

        if (input.BlockedSceneDates is { Count: > 0 })
        {
            reasons.Add($"{input.BlockedSceneDates.Count} scene-day(s) blocked by the disruption being replanned");
        }

        return reasons.Count == 0
            ? "No schedule satisfies the current constraints."
            : "No schedule satisfies the current constraints — " + string.Join(", and ", reasons) + ".";
    }

    private SolverOutput Extract(
        SolverInput input,
        CpSolver solver,
        BoolVar[,] x,
        BoolVar[] used,
        BoolVar[] night,
        int numScenes,
        int numDays,
        CpSolverStatus status,
        bool isOptimal)
    {
        var scheduledDays = new List<ScheduledDayResult>();
        var anomalies = new List<Anomaly>();
        ShootDay? previousDay = null;

        for (int d = 0; d < numDays; d++)
        {
            if (solver.Value(used[d]) != 1)
            {
                continue;
            }

            var dayScenes = new List<Scene>();
            for (int s = 0; s < numScenes; s++)
            {
                if (solver.Value(x[s, d]) == 1)
                {
                    dayScenes.Add(input.Scenes[s]);
                }
            }

            if (dayScenes.Count == 0)
            {
                continue;
            }

            dayScenes = dayScenes.OrderBy(s => s.Location, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(s => s.Number)
                                 .ToList();

            bool isNightUnit = solver.Value(night[d]) == 1;
            var callTime = isNightUnit ? NightUnitCall : DayUnitCall;
            int workMinutes = dayScenes.Sum(SceneMinutes);
            int moves = dayScenes.Select(s => s.Location)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).Count() - 1;
            int elapsed = ShootDayModel.ElapsedMinutes(workMinutes, moves);
            var wrapTime = callTime.AddMinutes(elapsed);

            // Production day numbers are sequential over the days actually shot; the date
            // is the calendar date, which may skip a day the constraints ruled out.
            int dayNumber = scheduledDays.Count + 1;
            var date = input.ScheduleStartDate.AddDays(d);

            // The location the unit is based at: the one occupying most of the day.
            var primaryLocation = dayScenes
                .GroupBy(s => s.Location, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(SceneMinutes))
                .First().Key;

            var shootDay = new ShootDay(Guid.NewGuid(), date, dayNumber, primaryLocation, callTime, wrapTime);

            if (previousDay is not null)
            {
                var turnaround = _unionRulesService.ValidateTurnaround(previousDay, shootDay);
                if (turnaround is not null)
                {
                    anomalies.Add(turnaround);
                }
            }

            // ValidateMealPenalty takes the longest *continuous* stretch, not the total. The
            // day reserves a meal break once it runs past six hours, which splits the work
            // in two; passing the total was reporting a missing break the schedule has.
            var meal = _unionRulesService.ValidateMealPenalty(
                shootDay, TimeSpan.FromMinutes(ShootDayModel.LongestContinuousStretch(workMinutes)));
            if (meal is not null)
            {
                anomalies.Add(meal);
            }

            scheduledDays.Add(new ScheduledDayResult(dayNumber, date, primaryLocation, callTime, wrapTime, dayScenes));
            previousDay = shootDay;
        }

        int locationDays = scheduledDays
            .Sum(day => day.ScheduledScenes.Select(s => s.Location)
                           .Distinct(StringComparer.OrdinalIgnoreCase).Count());

        return new SolverOutput(
            IsOptimal: isOptimal,
            IsFeasible: true,
            ObjectiveValue: (long)solver.ObjectiveValue,
            ScheduledDays: scheduledDays,
            DetectedAnomalies: anomalies,
            SolverMessage: $"CP-SAT {status}: {scheduledDays.Count} shooting day(s), "
                         + $"{locationDays} location-day(s), {anomalies.Count} anomal"
                         + (anomalies.Count == 1 ? "y." : "ies.")
        );
    }
}
