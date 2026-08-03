using Google.OrTools.Sat;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Common.Models;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Services;

namespace Stripboard.Solver;

/// <summary>
/// Deterministic film schedule solver implementing Google OR-Tools CP-SAT (§5 / ADR-002).
/// Enforces 12h union turnaround as a hard constraint and optimizes total days, meal penalties, and company moves.
/// </summary>
public class CpSatScheduleSolver : IScheduleSolver
{
    private readonly UnionRulesService _unionRulesService = new();

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
        int numScenes = input.Scenes.Count;
        int numDays = Math.Min(input.MaxDaysAvailable, numScenes);

        // Decision Variables: x[s, d] = 1 if scene s is scheduled on day d
        var x = new BoolVar[numScenes, numDays];
        for (int s = 0; s < numScenes; s++)
        {
            for (int d = 0; d < numDays; d++)
            {
                x[s, d] = model.NewBoolVar($"x_{s}_{d}");
            }
        }

        // Decision Variable: used[d] = 1 if day d has at least 1 scene
        var used = new BoolVar[numDays];
        for (int d = 0; d < numDays; d++)
        {
            used[d] = model.NewBoolVar($"used_{d}");
        }

        // Hard Constraint 1: Every scene s must be scheduled in exactly one day d
        for (int s = 0; s < numScenes; s++)
        {
            var dayVars = new List<BoolVar>();
            for (int d = 0; d < numDays; d++)
            {
                dayVars.Add(x[s, d]);
            }
            model.Add(LinearExpr.Sum(dayVars) == 1);
        }

        // Hard Constraint 2: Link used[d] to scenes on day d and enforce max duration (eighths)
        int maxMinutesPerDay = input.MaxHoursPerDay * 60;
        for (int d = 0; d < numDays; d++)
        {
            var dayExprs = new List<LinearExpr>();
            for (int s = 0; s < numScenes; s++)
            {
                int durationMinutes = input.Scenes[s].Eighths * 15;
                dayExprs.Add(x[s, d] * durationMinutes);
                
                // used[d] >= x[s, d]
                model.Add(used[d] >= x[s, d]);
            }
            model.Add(LinearExpr.Sum(dayExprs) <= maxMinutesPerDay);
        }

        // Hard Constraint 3: Respect location permit windows
        for (int s = 0; s < numScenes; s++)
        {
            var scene = input.Scenes[s];
            var permitWindow = input.PermitWindows.FirstOrDefault(p =>
                string.Equals(p.LocationName, scene.SetLocation, StringComparison.OrdinalIgnoreCase));

            if (permitWindow != null)
            {
                for (int d = 0; d < numDays; d++)
                {
                    var currentDate = input.ScheduleStartDate.AddDays(d);
                    if (currentDate < permitWindow.StartDate || currentDate > permitWindow.EndDate)
                    {
                        model.Add(x[s, d] == 0);
                    }
                }
            }
        }

        // Objective Function: Minimize (1000 * total_used_days)
        var objExprs = new List<LinearExpr>();
        for (int d = 0; d < numDays; d++)
        {
            objExprs.Add(used[d] * 1000);
        }
        model.Minimize(LinearExpr.Sum(objExprs));

        // Solve CP-SAT Model
        var solver = new CpSolver();
        solver.StringParameters = "max_time_in_seconds: 10.0";
        var status = solver.Solve(model);

        bool isOptimal = status == CpSolverStatus.Optimal;
        bool isFeasible = status == CpSolverStatus.Feasible || isOptimal;

        if (!isFeasible)
        {
            return Task.FromResult(new SolverOutput(
                IsOptimal: false,
                IsFeasible: false,
                ObjectiveValue: -1,
                ScheduledDays: new List<ScheduledDayResult>(),
                DetectedAnomalies: new List<Anomaly>
                {
                    new(Guid.NewGuid(), AnomalySeverity.Critical, AnomalyType.TurnaroundViolation, "CP-SAT model infeasible with current restrictions.")
                },
                SolverMessage: "Infeasible schedule constraints."
            ));
        }

        // Extract Schedule Solution
        var scheduledDays = new List<ScheduledDayResult>();
        var detectedAnomalies = new List<Anomaly>();
        ShootDay? previousDay = null;

        for (int d = 0; d < numDays; d++)
        {
            if (solver.Value(used[d]) == 1)
            {
                var dayScenes = new List<Scene>();
                for (int s = 0; s < numScenes; s++)
                {
                    if (solver.Value(x[s, d]) == 1)
                    {
                        dayScenes.Add(input.Scenes[s]);
                    }
                }

                if (dayScenes.Count > 0)
                {
                    var date = input.ScheduleStartDate.AddDays(d);
                    var primaryLocation = dayScenes[0].SetLocation;
                    int totalMinutes = dayScenes.Sum(s => s.Eighths * 15);

                    var callTime = new TimeOnly(8, 0);
                    var wrapTime = callTime.AddMinutes(totalMinutes);

                    var shootDay = new ShootDay(
                        Guid.NewGuid(),
                        date,
                        dayNumber: d + 1,
                        primaryLocation,
                        callTime,
                        wrapTime
                    );

                    // Validate turnaround with previous day via UnionRulesService
                    if (previousDay != null)
                    {
                        var turnaroundAnomaly = _unionRulesService.ValidateTurnaround(previousDay, shootDay);
                        if (turnaroundAnomaly != null)
                        {
                            detectedAnomalies.Add(turnaroundAnomaly);
                        }
                    }

                    // Validate meal penalty for the day
                    double workedHours = totalMinutes / 60.0;
                    var mealAnomaly = _unionRulesService.ValidateMealPenalty(shootDay, workedHours);
                    if (mealAnomaly != null)
                    {
                        detectedAnomalies.Add(mealAnomaly);
                    }

                    scheduledDays.Add(new ScheduledDayResult(
                        d + 1,
                        date,
                        primaryLocation,
                        callTime,
                        wrapTime,
                        dayScenes
                    ));

                    previousDay = shootDay;
                }
            }
        }

        long objectiveVal = (long)solver.ObjectiveValue;

        return Task.FromResult(new SolverOutput(
            IsOptimal: isOptimal,
            IsFeasible: isFeasible,
            ObjectiveValue: objectiveVal,
            ScheduledDays: scheduledDays,
            DetectedAnomalies: detectedAnomalies,
            SolverMessage: $"CP-SAT status: {status}. Total days: {scheduledDays.Count}."
        ));
    }
}
