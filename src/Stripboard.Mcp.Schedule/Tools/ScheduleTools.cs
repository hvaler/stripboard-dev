using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Stripboard.Application.Common.Models;
using Stripboard.Infrastructure.Services;

namespace Stripboard.Mcp.Schedule.Tools;

/// <summary>
/// The schedule tools, exposed over the Model Context Protocol (EV-23).
///
/// These are adapters, not logic. Every one of them calls <see cref="ScheduleService"/> or
/// <see cref="ReplanService"/> — the same engine the web app and the Python agents use. The
/// server that used to sit here kept its own copy of scheduling, and because nothing ever
/// exercised it, that copy drifted: it committed without checking authorisation, and its
/// validate_rules ignored the version id it was handed and validated every day in the
/// database instead. A second implementation nobody watches is worse than none.
///
/// Two rules shape the signatures below:
///
/// - **Every tool takes an identity**, because MCP has no ambient caller and the governance
///   rule is the point of this system. `commit_schedule` is refused for anything but a human
///   Producer, and the refusal comes from the service, not from this file.
/// - **No tool takes a domain object.** `create_schedule` used to accept a whole SolverInput,
///   with lists of scenes and people inside it. That is a legal MCP schema and an unusable
///   one: no agent will assemble it correctly, and one that tries will invent the contents.
///   Scenes come from the database; the tool takes the handful of choices a producer makes.
/// </summary>
[McpServerToolType]
public sealed class ScheduleTools
{
    private readonly ScheduleService _schedules;
    private readonly ReplanService _replanner;
    private readonly CallerIdentityResolver _callers;

    public ScheduleTools(ScheduleService schedules, ReplanService replanner, CallerIdentityResolver callers)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _replanner = replanner ?? throw new ArgumentNullException(nameof(replanner));
        _callers = callers ?? throw new ArgumentNullException(nameof(callers));
    }

    [McpServerTool(Name = "get_schedule")]
    [Description("Read a shooting schedule: days, units, company moves, union violations, cost, "
               + "and the scenes on each day. Returns the committed schedule when no version is given.")]
    public async Task<object> GetScheduleAsync(
        [Description("Schedule version id. Omit for the currently committed schedule.")]
        Guid? versionId = null,
        CancellationToken ct = default)
    {
        var board = versionId is { } id
            ? await _schedules.GetBoardAsync(id, ct: ct)
            : await _schedules.GetActiveBoardAsync(ct);

        if (board is null)
        {
            // "No schedule" is an answer. An empty board would read as a shoot with zero days.
            throw new McpException(versionId is null
                ? "No schedule has been committed yet. Import a screenplay breakdown first."
                : $"Schedule version {versionId} does not exist.");
        }

        return Describe(board);
    }

    [McpServerTool(Name = "create_schedule")]
    [Description("Run the CP-SAT solver over the scenes currently in the database and store the "
               + "result as a new draft version. Does not commit it — only a Producer can do that.")]
    public async Task<object> CreateScheduleAsync(
        [Description("Identity of the caller, e.g. 'Producer' or 'sa-scheduler'. Used only when "
                   + "the platform did not already prove who you are.")]
        string identity,
        [Description("First shooting day, ISO format (YYYY-MM-DD).")]
        string startDate,
        [Description("Most shooting days the schedule may use. Omit to let the solver decide.")]
        int? maxDaysAvailable = null,
        [Description("Hard cap on how many locations one day may visit. Two means one company "
                   + "move; obeying a cap usually costs shooting days, and the result reports how many.")]
        int? maxLocationsPerDay = null,
        CancellationToken ct = default)
    {
        var start = IsoDate.Parse(startDate);

        try
        {
            var board = await _schedules.GenerateAsync(
                createdBy: _callers.Resolve(identity).Name,
                startDate: start,
                maxDaysAvailable: maxDaysAvailable,
                maxLocationsPerDay: maxLocationsPerDay,
                ct: ct);

            return Describe(board);
        }
        catch (ScheduleService.NotAuthorizedException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Infeasible, or nothing to schedule. Both are useful answers, not failures.
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "commit_schedule")]
    [Description("Commit a draft schedule version, making it the plan the production works to. "
               + "Only an authenticated human Producer may do this. An agent calling it will be "
               + "refused, and that refusal is the system working as designed.")]
    public async Task<object> CommitScheduleAsync(
        [Description("The schedule version to commit.")] Guid versionId,
        [Description("Identity of the caller. Only used when the platform proved nothing about "
                   + "you — in which case the commit is refused anyway, because a name in a "
                   + "request body is a claim rather than a credential.")]
        string? identity = null,
        CancellationToken ct = default)
    {
        try
        {
            // The resolved caller wins over anything the payload said. That is the whole
            // point: an agent could otherwise send identity="Producer" and commit.
            var board = await _schedules.CommitAsync(versionId, _callers.Resolve(identity), ct);
            return new
            {
                committed = true,
                board.VersionNumber,
                days = board.Metrics.TotalDays,
                costUsd = board.Metrics.EstimatedCostUsd,
            };
        }
        catch (ScheduleService.NotAuthorizedException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "validate_rules")]
    [Description("Check a schedule version against the union rules — 12-hour turnaround including "
               + "midnight crossing, meal penalties, night-to-day transitions — and return every "
               + "violation found. An empty list means the schedule is clean.")]
    public async Task<object> ValidateRulesAsync(
        [Description("Schedule version id. Omit for the currently committed schedule.")]
        Guid? versionId = null,
        CancellationToken ct = default)
    {
        var board = versionId is { } id
            ? await _schedules.GetBoardAsync(id, ct: ct)
            : await _schedules.GetActiveBoardAsync(ct);

        if (board is null)
        {
            throw new McpException(versionId is null
                ? "No schedule has been committed yet, so there is nothing to validate."
                : $"Schedule version {versionId} does not exist.");
        }

        return new
        {
            versionId = board.VersionId,
            board.VersionNumber,
            violations = board.Anomalies.Select(a => new
            {
                type = a.Type.ToString(),
                severity = a.Severity.ToString(),
                a.Message,
                scenes = a.SceneIds,
            }),
            clean = board.Anomalies.Count == 0,
        };
    }

    [McpServerTool(Name = "consolidate_schedule")]
    [Description("Price a constraint rather than absorb a disruption: re-solve with a hard cap on "
               + "locations per day and report what obeying it costs. Use when the schedule itself is "
               + "poor — a day hopping between locations — rather than when something has gone wrong.")]
    public async Task<object> ConsolidateAsync(
        [Description("The most locations a single shooting day may visit.")] int maxLocationsPerDay,
        CancellationToken ct = default)
    {
        try
        {
            var (current, consolidated) = await _replanner.ProposeConsolidationAsync(
                maxLocationsPerDay, ct: ct);

            return new { options = new[] { current, consolidated }.Select(DescribeOption) };
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static object Describe(ScheduleBoard board) => new
    {
        versionId = board.VersionId,
        board.VersionNumber,
        board.IsCommitted,
        board.CreatedBy,
        days = board.Metrics.TotalDays,
        companyMoves = board.Metrics.CompanyMoves,
        unionViolations = board.Metrics.UnionViolations,
        costUsd = board.Metrics.EstimatedCostUsd,
        isOptimal = board.Metrics.IsOptimal,
        schedule = board.Days.Select(d => new
        {
            d.DayNumber,
            date = d.Date.ToString("yyyy-MM-dd"),
            unit = d.CallTime.Hour >= 12 ? "night" : "day",
            call = d.CallTime.ToString("HH:mm"),
            wrap = d.WrapTime.ToString("HH:mm"),
            locations = d.Locations,
            scenes = d.Scenes.Select(s => s.Number),
        }),
    };

    // An infeasible option reports null rather than zero: zeros read as measurements, and
    // "this option costs nothing" is the opposite of "this option does not exist".
    private static object DescribeOption(ReplanOption option) => new
    {
        versionId = option.IsFeasible ? option.VersionId : (Guid?)null,
        option.Title,
        option.Strategy,
        option.Justification,
        option.IsFeasible,
        option.SameFiguresAs,
        days = option.IsFeasible ? option.Metrics.TotalDays : (int?)null,
        companyMoves = option.IsFeasible ? option.Metrics.CompanyMoves : (int?)null,
        unionViolations = option.IsFeasible ? option.Metrics.UnionViolations : (int?)null,
        costUsd = option.IsFeasible ? option.Metrics.EstimatedCostUsd : (decimal?)null,
        delta = option.IsFeasible
            ? new
            {
                option.Delta.ExtraShootDays,
                option.Delta.ExtraCompanyMoves,
                option.Delta.ExtraUnionViolations,
                option.Delta.CostDeltaUsd,
            }
            : null,
    };
}
