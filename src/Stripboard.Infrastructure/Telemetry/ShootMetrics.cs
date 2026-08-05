using System.Diagnostics.Metrics;
using Stripboard.Application.Common.Models;

namespace Stripboard.Infrastructure.Telemetry;

/// <summary>
/// Telemetry about the shoot, not about the software (EV-29).
///
/// Almost any entry in this hackathon can point Grafana at its own request latency. The
/// thing worth observing here is the production: a shoot is a real-time system with a
/// budget burning down, a risk profile that moves, and actors being paid whether or not
/// they are called. Those are the signals a 1st AD would put on a wall, so those are what
/// this exports.
///
/// Instrument units are written in braces where the name already carries the unit — the
/// OpenTelemetry-to-Prometheus mangler appends a real unit as a suffix, which would turn
/// shoot.cost_estimate_usd into shoot_cost_estimate_usd_USD and break every query written
/// against it.
///
/// Values are pushed in whenever a schedule is produced, and read back by observable
/// gauges at scrape time. The alternative — querying the database from inside a metrics
/// callback — puts EF Core on the exporter's thread for no benefit.
/// </summary>
public sealed class ShootMetrics : IDisposable
{
    public const string MeterName = "Stripboard.Shoot";

    private readonly Meter _meter;
    private readonly object _gate = new();
    private ScheduleBoard? _board;

    public ShootMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _meter.CreateObservableGauge("shoot.days_total", () => Read(b => (double)b.Metrics.TotalDays),
            unit: "{day}", description: "Shooting days in the committed schedule.");

        _meter.CreateObservableGauge("shoot.company_moves", () => Read(b => (double)b.Metrics.CompanyMoves),
            unit: "{move}", description: "Times the unit changes location across the shoot. Each one costs an hour of the day.");

        _meter.CreateObservableGauge("shoot.cost_estimate_usd", () => Read(b => (double)b.Metrics.EstimatedCostUsd),
            unit: "{usd}", description: "Estimated cost of the committed schedule: crew and called cast per day, plus move and penalty costs.");

        _meter.CreateObservableGauge("shoot.union_violations", () => Read(b => (double)b.Metrics.UnionViolations),
            unit: "{violation}", description: "Union rule violations the domain layer found in the committed schedule.");

        _meter.CreateObservableGauge("shoot.scenes_total", () => Read(b => (double)b.Days.Sum(d => d.Scenes.Count)),
            unit: "{scene}", description: "Scenes in the committed schedule.");

        _meter.CreateObservableGauge("shoot.eighths_total", () => Read(b => (double)b.Metrics.TotalEighths),
            unit: "{eighth}", description: "Total script length scheduled, in eighths of a page.");

        _meter.CreateObservableGauge("shoot.locations_per_day_max",
            () => Read(b => (double)b.Days.Max(d => d.Locations.Count)),
            unit: "{location}", description:
            "Locations visited on the worst day of the shoot. Two is a move; three is a day "
            + "spent in the van. This is the single most actionable number on the board, "
            + "because the fix — consolidate that day — is one a 1st AD can act on.");

        _meter.CreateObservableGauge("shoot.risk_index", () => Read(RiskIndex),
            unit: "{index}", description:
            "Heuristic 0-100 index of how fragile the schedule is, not a probability: "
            + "15 per union violation, 3 per company move, 10 per day that visits more than one location.");

        _meter.CreateObservableGauge("shoot.cast_utilization", CastUtilization,
            unit: "{ratio}", description:
            "Fraction of shooting days each cast member is called for. A low value is an actor "
            + "being paid to wait, which is the cost a Day Out of Days schedule exists to avoid.");

        SolveDuration = _meter.CreateHistogram<double>("solver.solve_duration",
            unit: "ms", description: "Wall-clock time for one CP-SAT solve.");

        SolveCount = _meter.CreateCounter<long>("solver.solves_total",
            unit: "{solve}", description: "CP-SAT solves run, tagged by outcome.");
    }

    public Histogram<double> SolveDuration { get; }
    public Counter<long> SolveCount { get; }

    /// <summary>Publishes the schedule the gauges should describe from now on.</summary>
    public void Observe(ScheduleBoard board)
    {
        lock (_gate)
        {
            _board = board;
        }
    }

    /// <summary>
    /// Reports nothing until a schedule exists. This used to return 0, which put
    /// `shoot_union_violations 0` and `shoot_days_total 0` on the wire before anything had
    /// been solved — readings that say "a clean two-day shoot" when the truth is "nobody has
    /// scheduled anything". Alert rules read those zeros as healthy. An absent series is the
    /// honest signal, and it is the one `noDataState` exists for.
    /// </summary>
    private IEnumerable<Measurement<double>> Read(Func<ScheduleBoard, double> select)
    {
        ScheduleBoard? board;
        lock (_gate)
        {
            board = _board;
        }

        if (board is not null)
        {
            yield return new Measurement<double>(select(board));
        }
    }

    private static double RiskIndex(ScheduleBoard board)
    {
        var multiLocationDays = board.Days.Count(d => d.Locations.Count > 1);
        var score = board.Metrics.UnionViolations * 15
                  + board.Metrics.CompanyMoves * 3
                  + multiLocationDays * 10;
        return Math.Min(100, score);
    }

    private IEnumerable<Measurement<double>> CastUtilization()
    {
        ScheduleBoard? board;
        lock (_gate)
        {
            board = _board;
        }

        if (board is null || board.Days.Count == 0)
        {
            yield break;
        }

        var daysWorked = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in board.Days)
        {
            foreach (var name in day.Scenes.SelectMany(s => s.Cast).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                daysWorked[name] = daysWorked.GetValueOrDefault(name) + 1;
            }
        }

        foreach (var (actor, worked) in daysWorked)
        {
            yield return new Measurement<double>(
                (double)worked / board.Days.Count,
                new KeyValuePair<string, object?>("actor", actor));
        }
    }

    public void Dispose() => _meter.Dispose();
}
