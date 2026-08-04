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
/// Covers the seam EV-21 introduced: the UI reads persisted schedules produced by real
/// solver runs, so these tests exercise generate → persist → read back → commit.
/// </summary>
public class ScheduleServiceTests
{
    private static readonly Guid Holmes = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Watson = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly Start = new(2026, 8, 10);

    private static StripboardDbContext NewDb() => new(
        new DbContextOptionsBuilder<StripboardDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ScheduleService NewService(StripboardDbContext db) =>
        new(db, new CpSatScheduleSolver(), new AgentAuthorizationService());

    private static async Task SeedAsync(StripboardDbContext db)
    {
        db.People.AddRange(
            new Person(Holmes, "Sherlock Holmes", PersonRole.Cast, 1500m),
            new Person(Watson, "Dr. John Watson", PersonRole.Cast, 1200m),
            new Person(Guid.NewGuid(), "1st AD", PersonRole.FirstAssistantDirector, 900m));

        db.Scenes.AddRange(
            new Scene(Guid.NewGuid(), 1, "221B BAKER STREET", IntExt.Int, DayNight.Day, 8, new[] { Holmes }, null, "A"),
            new Scene(Guid.NewGuid(), 2, "221B BAKER STREET", IntExt.Int, DayNight.Day, 8, new[] { Holmes, Watson }, null, "B"),
            new Scene(Guid.NewGuid(), 3, "TOWER BRIDGE", IntExt.Ext, DayNight.Night, 8, new[] { Watson }, null, "C"));

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GenerateAsync_PersistsAVersionWithShootDaysTheBoardCanReadBack()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var board = await NewService(db).GenerateAsync(AgentAuthorizationService.RoleProducer, Start, commit: true);

        board.Days.Should().NotBeEmpty("the solver must produce shoot days");
        board.Days.SelectMany(d => d.Scenes).Should().HaveCount(3, "every scene must be scheduled exactly once");
        (await db.ShootDays.CountAsync(d => d.ScheduleVersionId == board.VersionId))
            .Should().Be(board.Days.Count, "days must be linked to their version, not orphaned");
    }

    [Fact]
    public async Task GenerateAsync_DerivesCostFromRealDayRates_NotFromAConstant()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var board = await NewService(db).GenerateAsync(AgentAuthorizationService.RoleProducer, Start);

        // Crew (900) is called every day; cast rates depend on who works. The floor is
        // therefore crew * days, and the figure must never be one of the literals the old
        // hardcoded UI displayed.
        board.Metrics.EstimatedCostUsd.Should().BeGreaterThan(900m * board.Days.Count - 1);
        board.Metrics.EstimatedCostUsd.Should().NotBe(1500m).And.NotBe(8500m);
    }

    [Fact]
    public async Task GenerateAsync_CountsEveryLocationChangeAsACompanyMove()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var board = await NewService(db).GenerateAsync(AgentAuthorizationService.RoleProducer, Start);

        // Two distinct locations appear in the shooting order, so at least one move exists.
        // Reporting zero here is the bug this assertion exists to prevent.
        board.Metrics.CompanyMoves.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GenerateAsync_PacksTheShootFromTheStartDateWithoutIdleGaps()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var board = await NewService(db).GenerateAsync(AgentAuthorizationService.RoleProducer, Start);

        // Minimising day count alone let the solver pick arbitrary slots, producing a
        // "Day 11 of 2" shoot that started ten days late.
        board.Days[0].Date.Should().Be(Start);
        board.Days.Select(d => d.DayNumber).Should().BeInAscendingOrder();
        board.Days.Should().OnlyContain(d => d.DayNumber <= board.Days.Count);

        for (var i = 1; i < board.Days.Count; i++)
        {
            (board.Days[i].Date.DayNumber - board.Days[i - 1].Date.DayNumber)
                .Should().Be(1, "consecutive shooting days must not have idle days between them");
        }
    }

    [Fact]
    public async Task GenerateAsync_RefusesAnIdentityThatMayNotSolve()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var act = () => NewService(db).GenerateAsync(AgentAuthorizationService.SaSentinel, Start);

        await act.Should().ThrowAsync<ScheduleService.NotAuthorizedException>(
            "the sentinel is a read-only watcher and must not be able to run the solver");
    }

    [Fact]
    public async Task CommitAsync_IsRefusedForAgentsAndAllowedForTheProducer()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var service = NewService(db);
        var draft = await service.GenerateAsync(AgentAuthorizationService.SaReplanner, Start);

        var agentAttempt = () => service.CommitAsync(draft.VersionId, AgentAuthorizationService.SaReplanner);
        await agentAttempt.Should().ThrowAsync<ScheduleService.NotAuthorizedException>(
            "agents propose; only a human Producer commits (ADR-002)");

        var committed = await service.CommitAsync(draft.VersionId, AgentAuthorizationService.RoleProducer);
        committed.IsCommitted.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_LeavesExactlyOneCommittedVersion()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var service = NewService(db);

        var first = await service.GenerateAsync(AgentAuthorizationService.RoleProducer, Start, commit: true);
        var second = await service.GenerateAsync(AgentAuthorizationService.RoleProducer, Start);
        await service.CommitAsync(second.VersionId, AgentAuthorizationService.RoleProducer);

        var committed = await db.ScheduleVersions.Where(v => v.IsCommitted).ToListAsync();
        committed.Should().ContainSingle().Which.Id.Should().Be(second.VersionId);
        first.VersionId.Should().NotBe(second.VersionId);
    }

    [Fact]
    public async Task GenerateAsync_WithoutScenes_ExplainsWhatIsMissing()
    {
        using var db = NewDb();

        var act = () => NewService(db).GenerateAsync(AgentAuthorizationService.RoleProducer, Start);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*breakdown*", "the operator needs to be told to import a screenplay first");
    }
}
