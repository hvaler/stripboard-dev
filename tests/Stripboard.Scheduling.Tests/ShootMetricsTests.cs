using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Common.Models;
using Stripboard.Application.Services;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Services;
using Stripboard.Infrastructure.Telemetry;
using Stripboard.Solver;

namespace Stripboard.Scheduling.Tests;

/// <summary>
/// These metrics drive alert rules in Grafana Cloud (infra/grafana/alert-rules.json), so what
/// they say when they know nothing matters as much as what they say when they know something.
/// </summary>
public class ShootMetricsTests
{
    private static ScheduleBoard Board(int days = 3, int violations = 2, decimal cost = 41600m) => new(
        VersionId: Guid.NewGuid(),
        VersionNumber: 1,
        IsCommitted: true,
        CreatedBy: "Producer",
        CreatedAt: new DateTime(2026, 8, 5, 6, 0, 0, DateTimeKind.Utc),
        Days: Enumerable.Range(1, days).Select(n => new BoardDay(
            DayNumber: n,
            Date: new DateOnly(2026, 8, 9).AddDays(n),
            LocationName: "221B BAKER STREET",
            CallTime: new TimeOnly(8, 0),
            WrapTime: new TimeOnly(18, 0),
            Scenes: [new BoardScene(n, "221B BAKER STREET", IntExt.Int, DayNight.Day, 8, "Scene", ["Sherlock Holmes"])],
            IsCompanyMove: false,
            TurnaroundHoursFromPreviousDay: 14,
            Locations: ["221B BAKER STREET"])).ToList(),
        Anomalies: [],
        Metrics: new ScheduleMetrics(days, 4, 24, cost, IsOptimal: true, violations),
        SolverMessage: "OPTIMAL");

    /// <summary>Collects one reading per instrument, or none if the instrument published none.</summary>
    private static Dictionary<string, List<double>> Collect(Action<ShootMetrics> arrange)
    {
        var readings = new Dictionary<string, List<double>>();
        using var metrics = new ShootMetrics();
        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ShootMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (!readings.TryGetValue(instrument.Name, out var values))
            {
                readings[instrument.Name] = values = [];
            }
            values.Add(value);
        });
        listener.Start();

        arrange(metrics);
        listener.RecordObservableInstruments();
        return readings;
    }

    [Fact]
    public void BeforeAnythingIsScheduled_NoGaugePublishesAValue()
    {
        // These used to publish 0, which put "shoot_union_violations 0" and "shoot_days_total 0"
        // on the wire before a single scene had been scheduled. A rule watching for union
        // violations reads that as a clean shoot. An absent series is the honest signal.
        var readings = Collect(_ => { });

        readings.Should().NotContainKey("shoot.days_total");
        readings.Should().NotContainKey("shoot.union_violations");
        readings.Should().NotContainKey("shoot.cost_estimate_usd");
        readings.Should().NotContainKey("shoot.risk_index");
        readings.Should().NotContainKey("shoot.locations_per_day_max");
    }

    [Fact]
    public void OnceAScheduleIsObserved_TheGaugesReportIt()
    {
        var readings = Collect(metrics => metrics.Observe(Board(days: 3, violations: 2, cost: 41600m)));

        readings["shoot.days_total"].Should().Equal(3);
        readings["shoot.union_violations"].Should().Equal(2);
        readings["shoot.cost_estimate_usd"].Should().Equal(41600);
        readings["shoot.scenes_total"].Should().Equal(3);
    }

    [Fact]
    public void TheWorstDayDrivesTheLocationsPerDayGauge()
    {
        // One good day does not make up for a day spent in the van, so this reports the
        // maximum rather than an average — an average would hide exactly the day that hurts.
        var board = Board(days: 3);
        var busiest = board.Days[1] with { Locations = ["221B BAKER STREET", "SCOTLAND YARD", "PICCADILLY"] };
        var withABadDay = board with { Days = [board.Days[0], busiest, board.Days[2]] };

        var readings = Collect(metrics => metrics.Observe(withABadDay));

        readings["shoot.locations_per_day_max"].Should().Equal(3);
    }

    [Fact]
    public async Task TheRefresherRepublishesWhateverIsCommitted_SoInstancesCannotDisagree()
    {
        // The bug: ShootMetrics caches the board in memory, and only the instance that
        // handled a commit called Observe. Every other instance carried on publishing the
        // schedule it last saw, so Grafana got two contradictory answers for the same
        // question and max() picked whichever was larger. Two plausible numbers, one of them
        // describing a shoot nobody was on.
        var databaseName = Guid.NewGuid().ToString();
        await using (var db = NewDb(databaseName))
        {
            await SeedAsync(db);
        }

        var services = ServicesFor(databaseName);
        using var metrics = new ShootMetrics();

        using var scope = services.CreateScope();
        var committed = await scope.ServiceProvider.GetRequiredService<ScheduleService>()
            .GenerateAsync(AgentAuthorizationService.RoleProducer, new DateOnly(2026, 8, 10), commit: true);

        // This ShootMetrics never saw the commit — it is standing in for the other instance.
        Read(metrics).Should().NotContainKey("shoot.days_total", "nothing has been observed yet");

        await new ShootMetricsRefresher(services, metrics).RefreshAsync();

        var readings = Read(metrics);
        readings["shoot.days_total"].Should().ContainSingle()
            .Which.Should().Be(committed.Metrics.TotalDays);
        readings["shoot.company_moves"].Should().ContainSingle()
            .Which.Should().Be(committed.Metrics.CompanyMoves);
    }

    private static StripboardDbContext NewDb(string name) => new(
        new DbContextOptionsBuilder<StripboardDbContext>().UseInMemoryDatabase(name).Options);

    private static async Task SeedAsync(StripboardDbContext db)
    {
        var holmes = Guid.NewGuid();
        db.People.AddRange(
            new Person(holmes, "Sherlock Holmes", PersonRole.Cast, 1500m),
            new Person(Guid.NewGuid(), "1st AD", PersonRole.FirstAssistantDirector, 900m));

        for (var i = 1; i <= 4; i++)
        {
            db.Scenes.Add(new Scene(Guid.NewGuid(), i,
                i <= 2 ? "221B BAKER STREET" : "SCOTLAND YARD",
                IntExt.Int, DayNight.Day, 8, [holmes], null, $"Scene {i}"));
        }
        await db.SaveChangesAsync();
    }

    /// <summary>A provider whose scopes all reach the same in-memory store, like one instance.</summary>
    private static ServiceProvider ServicesFor(string databaseName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<StripboardDbContext>(o => o.UseInMemoryDatabase(databaseName));
        services.AddScoped<IScheduleSolver, CpSatScheduleSolver>();
        services.AddScoped<AgentAuthorizationService>();
        services.AddScoped<ScheduleService>();
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, List<double>> Read(ShootMetrics metrics)
    {
        var readings = new Dictionary<string, List<double>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ShootMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (!readings.TryGetValue(instrument.Name, out var values))
            {
                readings[instrument.Name] = values = [];
            }
            values.Add(value);
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return readings;
    }

    [Fact]
    public void CastUtilisationIsReportedPerActorAsAFractionOfShootingDays()
    {
        // Every day in the fixture calls Holmes, so he is at 1.0. An actor below that is
        // being paid against days they do not work, which is what the alert rule watches.
        var readings = Collect(metrics => metrics.Observe(Board(days: 4)));

        readings["shoot.cast_utilization"].Should().Equal(1.0);
    }
}
