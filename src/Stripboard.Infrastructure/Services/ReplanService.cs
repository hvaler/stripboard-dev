using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Models;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Infrastructure.Persistence;

namespace Stripboard.Infrastructure.Services;

/// <summary>
/// Turns a disruption into ranked, costed replan options (EV-21).
///
/// Every option is a real CP-SAT run under different constraints, and every figure in a
/// <see cref="CostDelta"/> is the difference between two solved schedules. Nothing here is
/// estimated by a model or written by hand: the LLM's job (EV-24) will be to explain these
/// numbers, never to produce them.
/// </summary>
public class ReplanService
{
    private readonly StripboardDbContext _db;
    private readonly ScheduleService _schedules;

    public ReplanService(StripboardDbContext db, ScheduleService schedules)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
    }

    /// <summary>
    /// Records the disruption, then proposes alternatives. Returns the disruption together
    /// with one option per strategy, including strategies that turned out infeasible —
    /// "there is no way to absorb this without extra days" is itself a useful answer.
    /// </summary>
    public async Task<(Disruption Disruption, IReadOnlyList<ReplanOption> Options)> ProposeAsync(
        DisruptionRequest request,
        string actor = "sa-replanner",
        CancellationToken ct = default)
    {
        var baseline = await _schedules.GetActiveBoardAsync(ct)
                       ?? throw new InvalidOperationException("There is no schedule to replan. Generate one first.");

        var blocked = await ResolveBlockedScenesAsync(request, ct);
        if (blocked.Count == 0)
        {
            throw new InvalidOperationException(
                "This disruption does not affect any scheduled scene, so there is nothing to replan.");
        }

        var disruption = new Disruption(
            Guid.NewGuid(),
            DateTime.UtcNow,
            request.TriggerType,
            request.Description,
            personId: request.PersonId,
            expectedDurationDays: request.DurationDays);
        _db.Disruptions.Add(disruption);

        _db.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            DateTime.UtcNow,
            eventType: "DisruptionDetected",
            actor: actor,
            details: $"{request.TriggerType}: {request.Description} — {blocked.Count} scene-day(s) blocked.",
            relatedEntityId: disruption.Id));

        await _db.SaveChangesAsync(ct);

        var startDate = baseline.Days.Count > 0 ? baseline.Days[0].Date : request.StartDate;
        var options = new List<ReplanOption>
        {
            await TryStrategyAsync(
                title: "Option A — absorb within the existing window",
                strategy: "cover-day-swap",
                justification: "Re-solves the same shooting window with the blocked scene-days forbidden, "
                             + "letting the solver reshuffle cover scenes instead of adding days.",
                baseline: baseline, blocked: blocked, startDate: startDate,
                maxDays: baseline.Metrics.TotalDays, disruptionId: disruption.Id, actor: actor, ct: ct),

            await TryStrategyAsync(
                title: "Option B — extend the schedule",
                strategy: "extend-window",
                justification: $"Allows {request.DurationDays} additional shooting day(s) so blocked scenes can "
                             + "move beyond the disruption rather than compressing the rest of the shoot.",
                baseline: baseline, blocked: blocked, startDate: startDate,
                maxDays: baseline.Metrics.TotalDays + Math.Max(1, request.DurationDays),
                disruptionId: disruption.Id, actor: actor, ct: ct),
        };

        return (disruption, options);
    }

    private async Task<ReplanOption> TryStrategyAsync(
        string title,
        string strategy,
        string justification,
        ScheduleBoard baseline,
        IReadOnlyList<BlockedSceneDate> blocked,
        DateOnly startDate,
        int maxDays,
        Guid disruptionId,
        string actor,
        CancellationToken ct)
    {
        try
        {
            var board = await _schedules.GenerateAsync(
                createdBy: actor,
                startDate: startDate,
                blocked: blocked,
                maxDaysAvailable: maxDays,
                parentVersionId: baseline.VersionId,
                disruptionId: disruptionId,
                commit: false,
                ct: ct);

            var option = new ReplanOption(
                board.VersionId, title, strategy, justification, board.Metrics,
                Delta(baseline.Metrics, board.Metrics), IsFeasible: true);

            // Record the proposal's rationale on the append-only audit trail. The pages read
            // proposals back from here, so a reload or a second browser sees the same options
            // instead of relying on server-side session state.
            _db.AuditEvents.Add(new AuditEvent(
                Guid.NewGuid(),
                DateTime.UtcNow,
                eventType: ReplanProposedEvent,
                actor: actor,
                details: JsonSerializer.Serialize(new ProposalRecord(
                    disruptionId, title, strategy, justification, baseline.VersionId)),
                relatedEntityId: board.VersionId));
            await _db.SaveChangesAsync(ct);

            return option;
        }
        catch (InvalidOperationException ex)
        {
            return new ReplanOption(
                Guid.Empty, title, strategy,
                $"Not possible: {ex.Message}",
                new ScheduleMetrics(0, 0, 0, 0m, false, 0),
                new CostDelta(0, 0, 0, 0m),
                IsFeasible: false);
        }
    }

    public const string ReplanProposedEvent = "ReplanProposed";

    private record ProposalRecord(
        Guid DisruptionId, string Title, string Strategy, string Justification, Guid BaselineVersionId);

    /// <summary>
    /// Rebuilds the options for the most recent disruption from persisted state, so the
    /// proposals page survives a reload and shows the same thing to everyone.
    /// </summary>
    public async Task<(Disruption? Disruption, IReadOnlyList<ReplanOption> Options)> GetLatestProposalsAsync(
        CancellationToken ct = default)
    {
        var disruption = await _db.Disruptions.AsNoTracking()
            .OrderByDescending(d => d.Timestamp)
            .FirstOrDefaultAsync(ct);

        if (disruption is null)
        {
            return (null, Array.Empty<ReplanOption>());
        }

        var events = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.EventType == ReplanProposedEvent)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        var options = new List<ReplanOption>();
        foreach (var evt in events)
        {
            ProposalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<ProposalRecord>(evt.Details);
            }
            catch (JsonException)
            {
                continue;
            }

            if (record is null || record.DisruptionId != disruption.Id || evt.RelatedEntityId is not { } versionId)
            {
                continue;
            }

            var board = await _schedules.GetBoardAsync(versionId, ct: ct);
            var baseline = await _schedules.GetBoardAsync(record.BaselineVersionId, ct: ct);
            if (board is null || baseline is null)
            {
                continue;
            }

            options.Add(new ReplanOption(
                versionId, record.Title, record.Strategy, record.Justification,
                board.Metrics, Delta(baseline.Metrics, board.Metrics), IsFeasible: true));
        }

        return (disruption, options);
    }

    private static CostDelta Delta(ScheduleMetrics before, ScheduleMetrics after) => new(
        ExtraShootDays: after.TotalDays - before.TotalDays,
        ExtraCompanyMoves: after.CompanyMoves - before.CompanyMoves,
        ExtraUnionViolations: after.UnionViolations - before.UnionViolations,
        CostDeltaUsd: after.EstimatedCostUsd - before.EstimatedCostUsd);

    /// <summary>
    /// Translates a disruption into the scene/date pairs the solver must forbid. This is the
    /// only place that knows what each trigger type means for a schedule.
    /// </summary>
    private async Task<IReadOnlyList<BlockedSceneDate>> ResolveBlockedScenesAsync(
        DisruptionRequest request,
        CancellationToken ct)
    {
        var scenes = await _db.Scenes.AsNoTracking().ToListAsync(ct);
        var dates = Enumerable.Range(0, Math.Max(1, request.DurationDays))
            .Select(offset => request.StartDate.AddDays(offset))
            .ToList();

        var affected = request.TriggerType switch
        {
            TriggerType.CastUnavailability when request.PersonId is { } personId =>
                scenes.Where(s => s.CastPersonIds.Contains(personId)),

            TriggerType.WeatherAlert =>
                scenes.Where(s => s.IntExt is IntExt.Ext or IntExt.IntExt && MatchesLocation(s, request.LocationName)),

            TriggerType.PermitExpiration =>
                scenes.Where(s => MatchesLocation(s, request.LocationName)),

            _ => scenes.Where(s => MatchesLocation(s, request.LocationName)),
        };

        return affected
            .SelectMany(scene => dates.Select(date =>
                new BlockedSceneDate(scene.Number, date, request.Description)))
            .ToList();
    }

    private static bool MatchesLocation(Scene scene, string? locationName) =>
        !string.IsNullOrWhiteSpace(locationName)
        && scene.SetLocation.Contains(locationName, StringComparison.OrdinalIgnoreCase);
}
