using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripboard.Infrastructure.Services;

namespace Stripboard.Infrastructure.Telemetry;

/// <summary>
/// Keeps every instance's <see cref="ShootMetrics"/> pointed at the schedule that is actually
/// committed, by re-reading it from the database on a timer.
///
/// **The bug this exists to fix.** `ShootMetrics` holds the board in memory, and only the
/// instance that handled a commit called `Observe`. With more than one instance running —
/// which is the normal state, not an edge case — the others carried on publishing the
/// schedule they last saw. Grafana then received two contradictory answers for the same
/// question:
///
///     shoot_company_moves{instance="2ec7805c"} 4     ← the committed schedule
///     shoot_company_moves{instance="6ad93936"} 6     ← a schedule nobody is shooting
///
/// and the dashboard's `max()` picked whichever was larger, so the panels showed a blend of
/// two different productions. Six company moves and four company moves are both plausible
/// numbers; nothing about the display said one of them was stale.
///
/// The shoot is a single global fact that lives in Cloud SQL. Instances hold a cache of it,
/// so the cache has to expire.
///
/// **Why a timer rather than reading the database at scrape time.** ADR-014 rejected querying
/// inside the observable-gauge callback, and that reasoning still holds: it would put EF Core
/// on the exporter's thread and make a slow database look like a broken exporter. A timer
/// keeps the read off that path and still converges every instance within one interval.
/// </summary>
public sealed class ShootMetricsRefresher : BackgroundService
{
    /// <summary>
    /// Comfortably shorter than a typical scrape interval, so a commit shows up on the next
    /// scrape rather than the one after. The read is one indexed query.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private readonly ShootMetrics _metrics;
    private readonly ILogger<ShootMetricsRefresher>? _logger;

    public ShootMetricsRefresher(
        IServiceProvider services,
        ShootMetrics metrics,
        ILogger<ShootMetricsRefresher>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            await RefreshAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>
    /// Reads the committed schedule and republishes it. Public so a test can drive one
    /// refresh and assert on the result, rather than starting the host and polling — a test
    /// that waits on a timer proves the timing, not the behaviour.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _services.CreateScope();
            var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();

            var board = await schedules.GetActiveBoardAsync(ct);
            if (board is not null)
            {
                _metrics.Observe(board);
            }

            // A null board is left alone deliberately. "No committed schedule" is already
            // represented by the gauges publishing nothing, and clearing a board we cannot
            // re-read would turn a transient database blip into a gap in the graph.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed refresh means the metrics go stale, not wrong — the instance keeps
            // publishing the last board it read. Worth a log, not worth crashing the host.
            _logger?.LogWarning(exception,
                "Could not refresh shoot metrics from the database; continuing with the last known schedule.");
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
