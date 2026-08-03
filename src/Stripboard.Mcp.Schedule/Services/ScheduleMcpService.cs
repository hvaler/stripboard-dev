using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Common.Models;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Services;
using Stripboard.Infrastructure.Persistence;

namespace Stripboard.Mcp.Schedule.Services;

/// <summary>
/// Service backing the mcp-schedule server endpoints (§6 / ADR-002 / ADR-004).
/// Provides get_schedule, create_schedule, commit_schedule, and validate_rules functionality.
/// </summary>
public class ScheduleMcpService
{
    private readonly StripboardDbContext _dbContext;
    private readonly IScheduleSolver _scheduleSolver;
    private readonly UnionRulesService _unionRulesService = new();

    public ScheduleMcpService(StripboardDbContext dbContext, IScheduleSolver scheduleSolver)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _scheduleSolver = scheduleSolver ?? throw new ArgumentNullException(nameof(scheduleSolver));
    }

    /// <summary>
    /// MCP Tool: get_schedule(version_id)
    /// </summary>
    public async Task<ScheduleVersion?> GetScheduleAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ScheduleVersions
            .FirstOrDefaultAsync(sv => sv.Id == versionId, cancellationToken);
    }

    /// <summary>
    /// MCP Tool: create_schedule(input)
    /// Runs CP-SAT solver and stores new draft ScheduleVersion.
    /// </summary>
    public async Task<SolverOutput> CreateScheduleAsync(SolverInput input, CancellationToken cancellationToken = default)
    {
        var solverResult = await _scheduleSolver.SolveAsync(input, cancellationToken);

        if (solverResult.IsFeasible)
        {
            var nextVersionNumber = (await _dbContext.ScheduleVersions.CountAsync(cancellationToken)) + 1;
            var draftVersion = new ScheduleVersion(
                Guid.NewGuid(),
                versionNumber: nextVersionNumber,
                createdBy: "mcp-schedule agent",
                isCommitted: false
            );

            _dbContext.ScheduleVersions.Add(draftVersion);

            var auditEvent = new AuditEvent(
                Guid.NewGuid(),
                DateTime.UtcNow,
                eventType: "ScheduleCreated",
                actor: "mcp-schedule",
                details: $"Draft schedule version {nextVersionNumber} created with {solverResult.ScheduledDays.Count} days.",
                relatedEntityId: draftVersion.Id
            );
            _dbContext.AuditEvents.Add(auditEvent);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return solverResult;
    }

    /// <summary>
    /// MCP Tool: commit_schedule(schedule_id, producer_id)
    /// Human-in-the-Loop approval (ADR-002).
    /// </summary>
    public async Task<ScheduleVersion> CommitScheduleAsync(Guid scheduleId, string producerId, CancellationToken cancellationToken = default)
    {
        var version = await _dbContext.ScheduleVersions
            .FirstOrDefaultAsync(sv => sv.Id == scheduleId, cancellationToken);

        if (version == null)
        {
            throw new KeyNotFoundException($"Schedule version with ID {scheduleId} not found.");
        }

        version.Commit();

        var auditEvent = new AuditEvent(
            Guid.NewGuid(),
            DateTime.UtcNow,
            eventType: "ScheduleCommitted",
            actor: producerId,
            details: $"Schedule version {version.VersionNumber} committed by Producer {producerId}.",
            relatedEntityId: version.Id
        );
        _dbContext.AuditEvents.Add(auditEvent);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return version;
    }

    /// <summary>
    /// MCP Tool: validate_rules(schedule_id)
    /// Evaluates union rules over current schedule days.
    /// </summary>
    public async Task<List<Anomaly>> ValidateRulesAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var anomalies = new List<Anomaly>();
        var shootDays = await _dbContext.ShootDays
            .OrderBy(sd => sd.Date)
            .ToListAsync(cancellationToken);

        ShootDay? previous = null;
        foreach (var day in shootDays)
        {
            if (previous != null)
            {
                var turnaroundAnomaly = _unionRulesService.ValidateTurnaround(previous, day);
                if (turnaroundAnomaly != null)
                {
                    anomalies.Add(turnaroundAnomaly);
                }
            }

            var workedHours = (day.EstimatedWrapTime.ToTimeSpan() - day.CallTime.ToTimeSpan()).TotalHours;
            var mealAnomaly = _unionRulesService.ValidateMealPenalty(day, TimeSpan.FromHours(workedHours));
            if (mealAnomaly != null)
            {
                anomalies.Add(mealAnomaly);
            }

            previous = day;
        }

        return anomalies;
    }
}
