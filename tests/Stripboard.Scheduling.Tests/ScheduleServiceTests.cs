using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Models;
using Stripboard.Application.Services;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Services;
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
    public async Task GenerateAsync_CountsAMoveWhenOneDayVisitsTwoLocations()
    {
        // Four short day scenes across two places fit inside one twelve-hour day, so the
        // unit packs up and drives while the light is burning. That is the hour the solver
        // charges for, and reporting zero moves for it was the original bug here.
        using var db = NewDb();
        db.People.Add(new Person(Holmes, "Sherlock Holmes", PersonRole.Cast, 1500m));
        for (var i = 1; i <= 4; i++)
        {
            db.Scenes.Add(new Scene(Guid.NewGuid(), i, i <= 2 ? "221B BAKER STREET" : "SCOTLAND YARD",
                IntExt.Int, DayNight.Day, 2, [Holmes], null, $"Scene {i}"));
        }
        await db.SaveChangesAsync();

        var board = await NewService(db).GenerateAsync(AgentAuthorizationService.RoleProducer, Start);

        board.Days.Should().ContainSingle("all four short scenes fit in one day");
        board.Days[0].Locations.Should().HaveCount(2);
        board.Metrics.CompanyMoves.Should().Be(1);
    }

    [Fact]
    public async Task GenerateAsync_DoesNotCountAnOvernightRelocationAsACompanyMove()
    {
        // The seeded fixture splits day and night units, so the unit relocates between wrap
        // and call. That costs no shooting time, and the solver's day-length model does not
        // charge for it either. Counting it here made this figure disagree with the model
        // that produced the schedule — and hid the benefit of consolidating a hopping day,
        // because the number was mostly overnight travel that consolidation cannot remove.
        using var db = NewDb();
        await SeedAsync(db);

        var board = await NewService(db).GenerateAsync(AgentAuthorizationService.RoleProducer, Start);

        board.Days.Should().HaveCount(2);
        board.Days.Should().OnlyContain(d => d.Locations.Count == 1, "no day visits two places");
        board.Metrics.CompanyMoves.Should().Be(0);
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

    private static CallerIdentity Producer =>
        CallerIdentity.FromHumanSession(AgentAuthorizationService.RoleProducer);

    [Fact]
    public async Task CommitAsync_IsRefusedForAgentsAndAllowedForTheProducer()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var service = NewService(db);
        var draft = await service.GenerateAsync(AgentAuthorizationService.SaReplanner, Start);

        var agentAttempt = () => service.CommitAsync(draft.VersionId,
            CallerIdentity.FromToken(AgentAuthorizationService.SaReplanner));
        await agentAttempt.Should().ThrowAsync<ScheduleService.NotAuthorizedException>(
            "agents propose; only a human Producer commits (ADR-002)");

        var committed = await service.CommitAsync(draft.VersionId, Producer);
        committed.IsCommitted.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_RefusesAnIdentityNothingVerified()
    {
        // The string overload builds an asserted identity on purpose, so the old call shape
        // — commit(versionId, "Producer") — now fails. That call is a caller vouching for
        // itself, which is precisely what an agent would do to get around ADR-002.
        using var db = NewDb();
        await SeedAsync(db);
        var service = NewService(db);
        var draft = await service.GenerateAsync(AgentAuthorizationService.RoleProducer, Start);

        var act = () => service.CommitAsync(draft.VersionId, AgentAuthorizationService.RoleProducer);

        (await act.Should().ThrowAsync<ScheduleService.NotAuthorizedException>())
            .WithMessage("*nothing verified it*");
    }

    [Fact]
    public async Task CommitAsync_LeavesExactlyOneCommittedVersion()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var service = NewService(db);

        var first = await service.GenerateAsync(AgentAuthorizationService.RoleProducer, Start, commit: true);
        var second = await service.GenerateAsync(AgentAuthorizationService.RoleProducer, Start);
        await service.CommitAsync(second.VersionId, Producer);

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

    // ── EV-37: the proposer and the approver are two different people ──────────────────
    //
    // The board used to read "Committed · created by sa-replanner", because one field carried
    // both. That is a service account presented as the approver of the one rule that exists to
    // keep service accounts out, and a reader believes the screen over the README.

    [Fact]
    public async Task ACommittedVersionRecordsWhoApprovedIt_NotOnlyWhoProposedIt()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var service = NewService(db);

        // Proposed by an agent, which is the normal case.
        var draft = await service.GenerateAsync(AgentAuthorizationService.SaReplanner, Start);
        draft.CreatedBy.Should().Be(AgentAuthorizationService.SaReplanner);
        draft.ApprovedBy.Should().BeNull("a draft has not been approved by anybody");

        var committed = await service.CommitAsync(draft.VersionId, Producer);

        committed.CreatedBy.Should().Be(AgentAuthorizationService.SaReplanner, "the agent still proposed it");
        committed.ApprovedBy.Should().Be(AgentAuthorizationService.RoleProducer);
        committed.ApprovedBy.Should().NotBe(committed.CreatedBy);
        committed.ApprovedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task NoServiceAccountCanEverAppearAsTheApprover()
    {
        // The acceptance criterion, asserted rather than described. Every agent identity the
        // system knows about is tried, and none of them reaches the approver field — because
        // the commit is refused before it can, not because the field is written carefully.
        await using var db = NewDb();
        await SeedAsync(db);
        var service = NewService(db);

        // One draft, proposed by an agent that is allowed to run the solver — sa-sentinel is
        // not one of them, which is a separate rule and a good sign: the watcher cannot even
        // produce a schedule, let alone approve one.
        var draft = await service.GenerateAsync(AgentAuthorizationService.SaReplanner, Start);

        string[] serviceAccounts =
        [
            AgentAuthorizationService.SaReplanner,
            AgentAuthorizationService.SaScheduler,
            AgentAuthorizationService.SaOrchestrator,
            AgentAuthorizationService.SaSentinel,
        ];

        foreach (var account in serviceAccounts)
        {
            // Authenticated by the platform, and still not a Producer.
            var attempt = () => service.CommitAsync(draft.VersionId, CallerIdentity.FromToken(account));
            await attempt.Should().ThrowAsync<ScheduleService.NotAuthorizedException>();
        }

        var committedVersions = await db.ScheduleVersions.Where(v => v.IsCommitted).ToListAsync();
        committedVersions.Should().BeEmpty("not one of those attempts should have committed anything");

        db.ScheduleVersions.Select(v => v.ApprovedBy)
            .Should().OnlyContain(a => a == null, "an unapproved version names no approver");
    }

    [Fact]
    public void TheDomainRefusesToCommitWithoutNamingAnApprover()
    {
        // Belt to the service's braces. If a future caller reaches the entity directly, an
        // empty approver is rejected there too — that is how "created by" came to stand in
        // for it the first time.
        var version = new ScheduleVersion(Guid.NewGuid(), versionNumber: 1);

        var act = () => version.Commit("  ");

        act.Should().Throw<ArgumentException>();
        version.IsCommitted.Should().BeFalse();
        version.ApprovedBy.Should().BeNull();
    }

    [Fact]
    public async Task TheBootstrapScheduleIsCommittedByNobody_AndSaysSo()
    {
        // A fresh instance solves and commits one schedule at startup so the board is not
        // empty. Nobody approved it, and the honest record of that is an absent approver —
        // not the proposer's name borrowed to fill the gap.
        await using var db = NewDb();
        await SeedAsync(db);
        var service = NewService(db);

        var bootstrap = await service.GenerateAsync(AgentAuthorizationService.RoleProducer, Start, commit: true);

        bootstrap.IsCommitted.Should().BeTrue();
        bootstrap.ApprovedBy.Should().BeNull("nobody approved the bootstrap schedule");
        bootstrap.ApprovedAt.Should().BeNull();
    }

    // ── EV-42: the agreement is configuration, not physics ────────────────────────────

    [Fact]
    public async Task ChangingTheUnionAgreementChangesTheScheduleTheSolverProduces()
    {
        // The acceptance criterion, and the reason the profile is worth having: eleven hours
        // of rest permits a thirteen-hour day where twelve hours permits only twelve, so the
        // same screenplay needs fewer days. If this ever passes with equal day counts, the
        // agreement has stopped reaching the solver and is decorating the warnings instead.
        var databaseName = Guid.NewGuid().ToString();

        async Task<ScheduleBoard> ScheduleUnder(UnionAgreement agreement)
        {
            await using var db = new StripboardDbContext(
                new DbContextOptionsBuilder<StripboardDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await SeedManyFullPageScenesAsync(db);

            var service = new ScheduleService(
                db, new CpSatScheduleSolver(), new AgentAuthorizationService(),
                metrics: null, agreement: agreement);

            return await service.GenerateAsync(AgentAuthorizationService.RoleProducer, Start);
        }

        var american = await ScheduleUnder(UnionAgreement.IatseSagAftra);
        var european = await ScheduleUnder(UnionAgreement.EuropeanDailyRest);

        UnionAgreement.IatseSagAftra.MaxHoursPerDay.Should().Be(12);
        UnionAgreement.EuropeanDailyRest.MaxHoursPerDay.Should().Be(13);

        european.Metrics.TotalDays.Should().BeLessThan(american.Metrics.TotalDays,
            "an eleven-hour rest permits a longer day, and a longer day needs fewer of them");
    }

    [Fact]
    public void TheLongestLawfulDayIsDerivedFromTheRestOwed()
    {
        // Not an independent setting. A day longer than this would leave less than the
        // required turnaround before the next call, so the schedule would be illegal by
        // arithmetic — deriving it is what stops the solver and the rule disagreeing.
        UnionAgreement.IatseSagAftra.MaxHoursPerDay.Should().Be(24 - 12);
        UnionAgreement.EuropeanDailyRest.MaxHoursPerDay.Should().Be(24 - 11);
    }

    [Fact]
    public void AnUnknownAgreementIsRefusedRatherThanQuietlyDefaulted()
    {
        // Falling back to the American figures for a European shoot would schedule it to the
        // wrong rest period and warn about nothing at all.
        var act = () => UnionAgreement.FromName("bectu-2019");

        act.Should().Throw<ArgumentOutOfRangeException>();
        UnionAgreement.FromName(null).Should().Be(UnionAgreement.IatseSagAftra);
        UnionAgreement.FromName("european").Should().Be(UnionAgreement.EuropeanDailyRest);
    }

    /// <summary>Enough full-page scenes that the length of a day decides the day count.</summary>
    private static async Task SeedManyFullPageScenesAsync(StripboardDbContext db)
    {
        var lead = Guid.NewGuid();
        db.People.AddRange(
            new Person(lead, "Lead", PersonRole.Cast, 1500m),
            new Person(Guid.NewGuid(), "1st AD", PersonRole.FirstAssistantDirector, 900m));

        // 24 eighths is a full three pages: at twelve hours only so many fit in a day.
        for (var i = 1; i <= 8; i++)
        {
            db.Scenes.Add(new Scene(Guid.NewGuid(), i, "221B BAKER STREET",
                IntExt.Int, DayNight.Day, 24, [lead], null, $"Scene {i}"));
        }
        await db.SaveChangesAsync();
    }
}
