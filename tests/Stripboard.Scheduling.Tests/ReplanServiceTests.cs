using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Models;
using Stripboard.Application.Services;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Services;
using Stripboard.Solver;

namespace Stripboard.Scheduling.Tests;

/// <summary>
/// The replanner used to return two hardcoded proposals with the literal figures $1,500 and
/// $8,500. These tests exist to make sure that can never come back: every option must be a
/// real solver run and every delta the difference between two solved schedules.
/// </summary>
public class ReplanServiceTests
{
    private static readonly Guid Holmes = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Watson = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly Start = new(2026, 8, 10);

    private static StripboardDbContext NewDb() => new(
        new DbContextOptionsBuilder<StripboardDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(ScheduleService Schedules, ReplanService Replanner, ScheduleBoard Baseline)>
        ArrangeAsync(StripboardDbContext db)
    {
        db.People.AddRange(
            new Person(Holmes, "Sherlock Holmes", PersonRole.Cast, 1500m),
            new Person(Watson, "Dr. John Watson", PersonRole.Cast, 1200m),
            new Person(Guid.NewGuid(), "1st AD", PersonRole.FirstAssistantDirector, 900m));

        // Six full-page scenes: at 12h/day only two fit per day, so the shoot needs several
        // days and blocking one of them genuinely changes the answer.
        for (var i = 1; i <= 6; i++)
        {
            var cast = i % 2 == 0 ? new[] { Holmes } : new[] { Watson };
            var location = i <= 3 ? "221B BAKER STREET" : "TOWER BRIDGE";
            var intExt = i <= 3 ? IntExt.Int : IntExt.Ext;
            db.Scenes.Add(new Scene(Guid.NewGuid(), i, location, intExt, DayNight.Day, 24, cast, null, $"Scene {i}"));
        }
        await db.SaveChangesAsync();

        var schedules = new ScheduleService(db, new CpSatScheduleSolver(), new AgentAuthorizationService());
        var baseline = await schedules.GenerateAsync(AgentAuthorizationService.RoleProducer, Start, commit: true);
        return (schedules, new ReplanService(db, schedules), baseline);
    }

    [Fact]
    public async Task ProposeAsync_ProducesOptionsWhoseNumbersComeFromSolvedSchedules()
    {
        using var db = NewDb();
        var (_, replanner, baseline) = await ArrangeAsync(db);

        var (disruption, options) = await replanner.ProposeAsync(new DisruptionRequest(
            TriggerType.CastUnavailability, Start, 1, Holmes, null, "Holmes is ill."));

        disruption.TriggerType.Should().Be(TriggerType.CastUnavailability);
        options.Should().HaveCount(2);
        options.Should().Contain(o => o.IsFeasible,
            "otherwise the per-option assertions below would pass vacuously");

        foreach (var option in options.Where(o => o.IsFeasible))
        {
            option.Metrics.TotalDays.Should().BeGreaterThan(0);
            option.Delta.CostDeltaUsd.Should().Be(option.Metrics.EstimatedCostUsd - baseline.Metrics.EstimatedCostUsd);
            option.Delta.ExtraShootDays.Should().Be(option.Metrics.TotalDays - baseline.Metrics.TotalDays);
            option.Delta.CostDeltaUsd.Should().NotBe(1500m, "that was the old hardcoded figure");
            option.Delta.CostDeltaUsd.Should().NotBe(8500m, "that was the old hardcoded figure");
        }
    }

    [Fact]
    public async Task ProposeAsync_ActuallyHonoursTheBlockedDay()
    {
        using var db = NewDb();
        var (schedules, replanner, _) = await ArrangeAsync(db);

        var (_, options) = await replanner.ProposeAsync(new DisruptionRequest(
            TriggerType.CastUnavailability, Start, 1, Holmes, null, "Holmes is ill."));

        var holmesScenes = (await db.Scenes.AsNoTracking().ToListAsync())
            .Where(s => s.CastPersonIds.Contains(Holmes)).Select(s => s.Number).ToHashSet();

        holmesScenes.Should().NotBeEmpty("the fixture must actually give Holmes some scenes");
        options.Should().Contain(o => o.IsFeasible,
            "otherwise this test would assert nothing at all");

        foreach (var option in options.Where(o => o.IsFeasible))
        {
            var board = await schedules.GetBoardAsync(option.VersionId);
            var scheduledOnBlockedDay = board!.Days
                .Where(d => d.Date == Start)
                .SelectMany(d => d.Scenes.Select(s => s.Number));

            scheduledOnBlockedDay.Should().NotIntersectWith(holmesScenes,
                "the solver was told those scenes cannot happen on that date");
        }
    }

    [Fact]
    public async Task ProposeAsync_RejectsADisruptionThatAffectsNothing()
    {
        using var db = NewDb();
        var (_, replanner, _) = await ArrangeAsync(db);

        var act = () => replanner.ProposeAsync(new DisruptionRequest(
            TriggerType.WeatherAlert, Start, 1, null, "A LOCATION THAT DOES NOT EXIST", "Rain."));

        await act.Should().ThrowAsync<InvalidOperationException>(
            "claiming to have replanned around a disruption that touches no scene would be a lie");
    }

    [Fact]
    public async Task ProposeAsync_RecordsTheDisruptionAndItsProposalsOnTheAuditTrail()
    {
        using var db = NewDb();
        var (_, replanner, _) = await ArrangeAsync(db);

        await replanner.ProposeAsync(new DisruptionRequest(
            TriggerType.CastUnavailability, Start, 1, Holmes, null, "Holmes is ill."));

        (await db.AuditEvents.CountAsync(e => e.EventType == "DisruptionDetected")).Should().Be(1);
        (await db.AuditEvents.CountAsync(e => e.EventType == ReplanService.ReplanProposedEvent))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetLatestProposalsAsync_RebuildsTheSameOptionsAfterAReload()
    {
        using var db = NewDb();
        var (_, replanner, _) = await ArrangeAsync(db);
        var (_, original) = await replanner.ProposeAsync(new DisruptionRequest(
            TriggerType.CastUnavailability, Start, 1, Holmes, null, "Holmes is ill."));

        var (disruption, reloaded) = await replanner.GetLatestProposalsAsync();

        disruption.Should().NotBeNull();
        reloaded.Select(o => o.VersionId).Should()
            .BeEquivalentTo(original.Where(o => o.IsFeasible).Select(o => o.VersionId));
    }

    [Fact]
    public async Task ProposeAsync_WithoutABaselineSchedule_SaysSo()
    {
        using var db = NewDb();
        var schedules = new ScheduleService(db, new CpSatScheduleSolver(), new AgentAuthorizationService());
        var replanner = new ReplanService(db, schedules);

        var act = () => replanner.ProposeAsync(new DisruptionRequest(
            TriggerType.CastUnavailability, Start, 1, Holmes, null, "Holmes is ill."));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
