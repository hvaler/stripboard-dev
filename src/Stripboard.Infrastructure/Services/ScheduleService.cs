using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Common.Models;
using Stripboard.Application.Services;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Services;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Telemetry;

namespace Stripboard.Infrastructure.Services;

/// <summary>
/// Generates, persists and reads shooting schedules (EV-21).
///
/// This is the seam the UI was missing: before EV-21 the solver existed but nothing called
/// it, and the pages rendered hardcoded HTML. Every board the UI shows now comes from a
/// persisted <see cref="ScheduleVersion"/> produced by a real CP-SAT run.
/// </summary>
public class ScheduleService
{
    private readonly StripboardDbContext _db;
    private readonly IScheduleSolver _solver;
    private readonly AgentAuthorizationService _authorization;
    private readonly ShootMetrics? _metrics;
    private readonly UnionRulesService _unionRules = new();

    public ScheduleService(
        StripboardDbContext db,
        IScheduleSolver solver,
        AgentAuthorizationService authorization,
        ShootMetrics? metrics = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        // Optional so tests can construct the service without a metrics pipeline.
        _metrics = metrics;
    }

    public sealed class NotAuthorizedException(string message) : InvalidOperationException(message);

    /// <summary>
    /// Runs the solver and stores the result as a new schedule version.
    /// </summary>
    public async Task<ScheduleBoard> GenerateAsync(
        string createdBy,
        DateOnly? startDate = null,
        IReadOnlyList<BlockedSceneDate>? blocked = null,
        int? maxDaysAvailable = null,
        Guid? parentVersionId = null,
        Guid? disruptionId = null,
        bool commit = false,
        CancellationToken ct = default)
    {
        if (!_authorization.CanExecuteSolve(createdBy))
        {
            throw new NotAuthorizedException($"'{createdBy}' is not permitted to run the solver.");
        }

        var scenes = await _db.Scenes.AsNoTracking().OrderBy(s => s.Number).ToListAsync(ct);
        if (scenes.Count == 0)
        {
            throw new InvalidOperationException("There are no scenes to schedule. Import a screenplay breakdown first.");
        }

        var people = await _db.People.AsNoTracking().ToListAsync(ct);
        var start = startDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var input = new SolverInput(
            Scenes: scenes,
            CastAndCrew: people,
            PermitWindows: new List<LocationPermitWindow>(),
            ScheduleStartDate: start,
            MaxDaysAvailable: maxDaysAvailable ?? Math.Max(scenes.Count, 10),
            BlockedSceneDates: blocked?.ToList()
        );

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _solver.SolveAsync(input, ct);
        stopwatch.Stop();

        _metrics?.SolveDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        _metrics?.SolveCount.Add(1,
            new KeyValuePair<string, object?>("feasible", result.IsFeasible),
            new KeyValuePair<string, object?>("optimal", result.IsOptimal));

        if (!result.IsFeasible)
        {
            throw new InvalidOperationException(
                $"No feasible schedule exists under these constraints. {result.SolverMessage}");
        }

        var version = await PersistAsync(result, createdBy, parentVersionId, disruptionId, commit, ct);
        var board = await GetBoardAsync(version.Id, result.IsOptimal, result.SolverMessage, ct)
               ?? throw new InvalidOperationException("Schedule version disappeared immediately after being written.");

        if (commit)
        {
            _metrics?.Observe(board);
        }
        return board;
    }

    private async Task<ScheduleVersion> PersistAsync(
        SolverOutput result,
        string createdBy,
        Guid? parentVersionId,
        Guid? disruptionId,
        bool commit,
        CancellationToken ct)
    {
        var nextNumber = await _db.ScheduleVersions.AnyAsync(ct)
            ? await _db.ScheduleVersions.MaxAsync(v => v.VersionNumber, ct) + 1
            : 1;

        var version = new ScheduleVersion(
            Guid.NewGuid(),
            versionNumber: nextNumber,
            parentId: parentVersionId,
            createdBy: createdBy,
            disruptionId: disruptionId,
            isCommitted: commit);
        _db.ScheduleVersions.Add(version);

        var existingStrips = await _db.Strips.ToDictionaryAsync(s => s.SceneId, ct);

        foreach (var day in result.ScheduledDays)
        {
            var stripIds = new List<Guid>();
            var order = 1;
            foreach (var scene in day.ScheduledScenes)
            {
                if (!existingStrips.TryGetValue(scene.Id, out var strip))
                {
                    strip = new Strip(Guid.NewGuid(), scene.Id, order, Math.Max(1, scene.Eighths * 15));
                    _db.Strips.Add(strip);
                    existingStrips[scene.Id] = strip;
                }
                stripIds.Add(strip.Id);
                order++;
            }

            _db.ShootDays.Add(new ShootDay(
                Guid.NewGuid(),
                day.Date,
                day.DayNumber,
                day.LocationName,
                day.CallTime,
                day.WrapTime,
                stripIds,
                version.Id));
        }

        _db.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            DateTime.UtcNow,
            eventType: commit ? "ScheduleCommitted" : "ScheduleDrafted",
            actor: createdBy,
            details: $"Schedule v{nextNumber} produced by CP-SAT: {result.ScheduledDays.Count} shoot days, "
                   + $"objective {result.ObjectiveValue}, {result.DetectedAnomalies.Count} anomalies.",
            relatedEntityId: version.Id));

        await _db.SaveChangesAsync(ct);
        return version;
    }

    /// <summary>Commits a draft version. Only a human Producer may do this (ADR-002).</summary>
    public async Task<ScheduleBoard> CommitAsync(Guid versionId, string identity, CancellationToken ct = default)
    {
        if (!_authorization.CanExecuteCommit(identity))
        {
            throw new NotAuthorizedException(
                $"'{identity}' cannot commit a schedule. Only the Producer role may commit — agents propose, humans decide.");
        }

        var version = await _db.ScheduleVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct)
                      ?? throw new KeyNotFoundException($"Schedule version {versionId} not found.");

        foreach (var other in await _db.ScheduleVersions.Where(v => v.Id != versionId && v.IsCommitted).ToListAsync(ct))
        {
            _db.Entry(other).Property(nameof(ScheduleVersion.IsCommitted)).CurrentValue = false;
        }

        version.Commit();

        _db.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            DateTime.UtcNow,
            eventType: "ScheduleCommitted",
            actor: identity,
            details: $"Schedule v{version.VersionNumber} committed by {identity}.",
            relatedEntityId: version.Id));

        await _db.SaveChangesAsync(ct);
        var board = await GetBoardAsync(versionId, ct: ct)
               ?? throw new InvalidOperationException("Committed version could not be read back.");

        // The shoot the world should now be watching is the one that was just committed.
        _metrics?.Observe(board);
        return board;
    }

    public async Task<ScheduleBoard?> GetActiveBoardAsync(CancellationToken ct = default)
    {
        var version = await _db.ScheduleVersions.AsNoTracking()
                          .Where(v => v.IsCommitted)
                          .OrderByDescending(v => v.VersionNumber)
                          .FirstOrDefaultAsync(ct)
                      ?? await _db.ScheduleVersions.AsNoTracking()
                          .OrderByDescending(v => v.VersionNumber)
                          .FirstOrDefaultAsync(ct);

        return version is null ? null : await GetBoardAsync(version.Id, ct: ct);
    }

    /// <summary>Materialises a stored version into the read model the UI renders.</summary>
    public async Task<ScheduleBoard?> GetBoardAsync(
        Guid versionId,
        bool isOptimal = true,
        string solverMessage = "",
        CancellationToken ct = default)
    {
        var version = await _db.ScheduleVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null)
        {
            return null;
        }

        var days = await _db.ShootDays.AsNoTracking()
            .Where(d => d.ScheduleVersionId == versionId)
            .OrderBy(d => d.DayNumber)
            .ToListAsync(ct);

        var strips = await _db.Strips.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        var scenes = await _db.Scenes.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        var people = await _db.People.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);

        var crew = people.Values.Where(p => !p.IsCast).ToList();
        var boardDays = new List<BoardDay>();
        var anomalies = new List<Anomaly>();
        var companyMoves = 0;
        decimal cost = 0m;

        ShootDay? previous = null;
        string? lastLocation = null;
        foreach (var day in days)
        {
            var dayScenes = day.StripIds
                .Select(id => strips.TryGetValue(id, out var strip) ? strip : null)
                .Where(s => s is not null)
                .Select(s => scenes.TryGetValue(s!.SceneId, out var scene) ? scene : null)
                .Where(s => s is not null)
                .Select(s => s!)
                .OrderBy(s => s.Number)
                .ToList();

            var castCalled = dayScenes
                .SelectMany(s => s.CastPersonIds)
                .Distinct()
                .Select(id => people.TryGetValue(id, out var person) ? person : null)
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();

            // A company move is any change of location in the shooting order, including
            // moves inside a single day. Counting only day-to-day changes would report 0
            // moves for a day that hops between five sets, which is exactly the cost a
            // 1st AD is trying to avoid. (The solver does not yet minimise this — EV-27.)
            var dayLocations = dayScenes.Select(s => s.Location).ToList();
            foreach (var location in dayLocations)
            {
                if (lastLocation is not null && !string.Equals(lastLocation, location, StringComparison.OrdinalIgnoreCase))
                {
                    companyMoves++;
                }
                lastLocation = location;
            }

            var isMove = previous is not null &&
                         !string.Equals(previous.LocationName, day.LocationName, StringComparison.OrdinalIgnoreCase);

            var turnaround = previous is null
                ? 0
                : (day.GetCallDateTime() - previous.GetWrapDateTime()).TotalHours;

            if (previous is not null)
            {
                var anomaly = _unionRules.ValidateTurnaround(previous, day);
                if (anomaly is not null)
                {
                    anomalies.Add(anomaly);
                }
            }

            // Ask the union rule about the longest continuous stretch, which is what it
            // actually measures. Passing call-to-wrap would count the meal break itself as
            // work and report a missing break the day reserves.
            var workMinutes = dayScenes.Sum(s => Math.Max(15, s.Eighths * 15));
            var mealAnomaly = _unionRules.ValidateMealPenalty(
                day, TimeSpan.FromMinutes(ShootDayModel.LongestContinuousStretch(workMinutes)));
            if (mealAnomaly is not null)
            {
                anomalies.Add(mealAnomaly);
            }

            cost += CostModel.DayCost(crew, castCalled);

            boardDays.Add(new BoardDay(
                day.DayNumber,
                day.Date,
                day.LocationName,
                day.CallTime,
                day.EstimatedWrapTime,
                dayScenes.Select(s => new BoardScene(
                    s.Number, s.SetLocation, s.IntExt, s.DayNight, s.Eighths, s.Synopsis,
                    s.CastPersonIds
                        .Select(id => people.TryGetValue(id, out var p) ? p.Name : null)
                        .Where(n => n is not null).Select(n => n!).ToList())).ToList(),
                isMove,
                Math.Round(turnaround, 1),
                dayLocations.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

            previous = day;
        }

        cost += companyMoves * CostModel.CompanyMoveUsd;
        cost += anomalies.Count * CostModel.UnionViolationPenaltyUsd;

        var metrics = new ScheduleMetrics(
            TotalDays: boardDays.Count,
            CompanyMoves: companyMoves,
            TotalEighths: boardDays.Sum(d => d.Scenes.Sum(s => s.Eighths)),
            EstimatedCostUsd: cost,
            IsOptimal: isOptimal,
            UnionViolations: anomalies.Count);

        return new ScheduleBoard(
            version.Id, version.VersionNumber, version.IsCommitted, version.CreatedBy, version.CreatedAt,
            boardDays, anomalies, metrics, solverMessage);
    }
}
