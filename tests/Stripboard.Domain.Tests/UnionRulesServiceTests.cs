using FluentAssertions;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Domain.Services;
using Xunit;

namespace Stripboard.Domain.Tests;

public class UnionRulesServiceTests
{
    private readonly UnionRulesService _service = new();

    [Theory]
    [InlineData(11, 59, true)]  // 11h 59m -> Rest < 12h -> Violation
    [InlineData(12, 0, false)]  // 12h 00m -> Rest == 12h -> Valid
    [InlineData(12, 1, false)]  // 12h 01m -> Rest > 12h -> Valid
    public void Turnaround_BoundaryCases_EvaluatesCorrectly(int restHours, int restMinutes, bool shouldBeViolation)
    {
        // Arrange
        var day1Date = new DateOnly(2026, 8, 10);
        var day1Call = new TimeOnly(7, 0);   // 7:00 AM
        var day1Wrap = new TimeOnly(19, 0);  // 7:00 PM wrap (12h work)
        var day1 = new ShootDay(Guid.NewGuid(), day1Date, 1, "STAGE A", day1Call, day1Wrap);

        var day2WrapDateTime = day1.GetWrapDateTime();
        var day2CallDateTime = day2WrapDateTime.AddHours(restHours).AddMinutes(restMinutes);

        var day2Date = DateOnly.FromDateTime(day2CallDateTime);
        var day2Call = TimeOnly.FromDateTime(day2CallDateTime);
        var day2Wrap = day2Call.AddHours(10);
        var day2 = new ShootDay(Guid.NewGuid(), day2Date, 2, "STAGE A", day2Call, day2Wrap);

        // Act
        var anomaly = _service.ValidateTurnaround(day1, day2);

        // Assert
        if (shouldBeViolation)
        {
            anomaly.Should().NotBeNull();
            anomaly!.Type.Should().Be(AnomalyType.TurnaroundViolation);
            anomaly.Severity.Should().Be(AnomalySeverity.Critical);
        }
        else
        {
            anomaly.Should().BeNull();
        }
    }

    [Fact]
    public void Turnaround_MidnightCrossing_ViolationDetected()
    {
        // Arrange: Overnight shoot wrapping at 02:00 AM on Day 2
        var day1Date = new DateOnly(2026, 8, 10);
        var day1Call = new TimeOnly(18, 0);  // 6:00 PM Aug 10
        var day1Wrap = new TimeOnly(2, 0);   // 2:00 AM Aug 11 (Overnight wrap)
        var day1 = new ShootDay(Guid.NewGuid(), day1Date, 1, "EXT BACKLOT", day1Call, day1Wrap);

        // Day 2 call at 13:00 PM Aug 11 -> Rest is 11 hours (02:00 to 13:00)
        var day2Date = new DateOnly(2026, 8, 11);
        var day2Call = new TimeOnly(13, 0);  // 1:00 PM Aug 11
        var day2Wrap = new TimeOnly(23, 0);
        var day2 = new ShootDay(Guid.NewGuid(), day2Date, 2, "EXT BACKLOT", day2Call, day2Wrap);

        // Act
        var anomaly = _service.ValidateTurnaround(day1, day2);

        // Assert
        anomaly.Should().NotBeNull();
        anomaly!.Type.Should().Be(AnomalyType.TurnaroundViolation);
        anomaly.Severity.Should().Be(AnomalySeverity.Critical);
    }

    [Theory]
    [InlineData(6, 0, false)]  // 6h 00m -> Valid
    [InlineData(6, 1, true)]   // 6h 01m -> Exceeds 6h limit -> MealPenaltyRisk
    public void MealPenalty_ContinuousWorkDuration_EvaluatesCorrectly(int workHours, int workMinutes, bool shouldBePenalty)
    {
        // Arrange
        var shootDay = new ShootDay(Guid.NewGuid(), new DateOnly(2026, 8, 10), 1, "STAGE B", new TimeOnly(8, 0), new TimeOnly(18, 0));
        var workDuration = TimeSpan.FromHours(workHours) + TimeSpan.FromMinutes(workMinutes);

        // Act
        var anomaly = _service.ValidateMealPenalty(shootDay, workDuration);

        // Assert
        if (shouldBePenalty)
        {
            anomaly.Should().NotBeNull();
            anomaly!.Type.Should().Be(AnomalyType.MealPenaltyRisk);
            anomaly.Severity.Should().Be(AnomalySeverity.High);
        }
        else
        {
            anomaly.Should().BeNull();
        }
    }

    [Fact]
    public void NightDayTransition_TightRest_ReturnsNightDayTransitionAnomaly()
    {
        // Arrange: Night shoot wrapping at 04:00 AM, next call at 16:00 PM (12h rest, but tight circadian transition)
        var day1Date = new DateOnly(2026, 8, 10);
        var day1Call = new TimeOnly(20, 0);  // 8:00 PM Aug 10
        var day1Wrap = new TimeOnly(4, 0);   // 4:00 AM Aug 11
        var day1 = new ShootDay(Guid.NewGuid(), day1Date, 1, "EXT NIGHT", day1Call, day1Wrap);

        var day2Date = new DateOnly(2026, 8, 11);
        var day2Call = new TimeOnly(16, 0);  // 4:00 PM Aug 11 (12h rest)
        var day2Wrap = new TimeOnly(2, 0);
        var day2 = new ShootDay(Guid.NewGuid(), day2Date, 2, "INT DAY", day2Call, day2Wrap);

        // Act
        var anomaly = _service.ValidateNightDayTransition(day1, day2, isPreviousNight: true, isCurrentDay: true);

        // Assert
        anomaly.Should().NotBeNull();
        anomaly!.Type.Should().Be(AnomalyType.NightDayTransition);
        anomaly.Severity.Should().Be(AnomalySeverity.High);
    }

    // ── found by mutation testing ──────────────────────────────────────────────
    //
    // Everything below kills a mutant that the suite above let live. Stryker rewrote the
    // rules — flipped an &&, moved a boundary, deleted a guard — and every existing test
    // still passed, which means those behaviours were never actually being checked. The
    // README claimed "union rules verified with mutation testing" long before any of this
    // existed; these are what make the claim true.

    /// <summary>Two shoot days with a chosen gap between wrap and the next call.</summary>
    private static (ShootDay Previous, ShootDay Current) DaysWithRest(double restHours)
    {
        var wrap = new TimeOnly(4, 0);                                 // 04:00 on Aug 11
        var call = new TimeOnly(4, 0).AddHours(restHours);
        return (
            new ShootDay(Guid.NewGuid(), new DateOnly(2026, 8, 10), 1, "EXT NIGHT", new TimeOnly(20, 0), wrap),
            new ShootDay(Guid.NewGuid(), new DateOnly(2026, 8, 11), 2, "INT DAY", call, call.AddHours(8)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void NightDayTransition_OnlyAppliesWhenANightIsFollowedByADay(bool previousNight, bool currentDay)
    {
        // The rule is about circadian whiplash: wrapping at dawn and being called back in
        // daylight. Mutation testing turned the `&&` into `||` and nothing failed, which
        // meant a day-to-day transition with 13 hours of rest would have been flagged as a
        // night transition — an anomaly a 1st AD would rightly ignore, teaching them to
        // ignore the rest.
        var (previous, current) = DaysWithRest(13);

        var anomaly = _service.ValidateNightDayTransition(previous, current, previousNight, currentDay);

        anomaly.Should().BeNull("this is not a night-to-day transition");
    }

    [Theory]
    [InlineData(13.99, true)]
    [InlineData(14.0, false)]
    [InlineData(14.01, false)]
    public void NightDayTransition_FlagsRestBelowFourteenHoursAndNotAtIt(double restHours, bool expectAnomaly)
    {
        // Exactly 14 hours was untested, so `< 14` and `<= 14` were indistinguishable. The
        // threshold is the rule; if the boundary is not pinned, the rule is not pinned.
        var (previous, current) = DaysWithRest(restHours);

        var anomaly = _service.ValidateNightDayTransition(previous, current, true, true);

        (anomaly is not null).Should().Be(expectAnomaly);
        if (anomaly is not null)
        {
            anomaly.Type.Should().Be(AnomalyType.NightDayTransition);
        }
    }

    [Fact]
    public void NightDayTransition_ReportsTheTurnaroundBreachRatherThanTheCircadianOne()
    {
        // Under 12 hours both rules fire. The turnaround one is the legal breach, so it is
        // the one that must come back — a High "tight for circadian adjustment" would
        // understate a Critical violation.
        var (previous, current) = DaysWithRest(10);

        var anomaly = _service.ValidateNightDayTransition(previous, current, true, true);

        anomaly.Should().NotBeNull();
        anomaly!.Type.Should().Be(AnomalyType.TurnaroundViolation);
        anomaly.Severity.Should().Be(AnomalySeverity.Critical);
    }

    [Fact]
    public void TheRulesRefuseNullDaysInsteadOfFailingLaterAndDeeper()
    {
        // Stryker deleted every ThrowIfNull guard and the suite stayed green. Without them a
        // null day surfaces as a NullReferenceException from somewhere inside the rule,
        // which is a much worse thing to debug at five in the morning.
        var day = new ShootDay(Guid.NewGuid(), new DateOnly(2026, 8, 10), 1, "STAGE", new TimeOnly(8, 0), new TimeOnly(18, 0));

        ((Action)(() => _service.ValidateTurnaround(null!, day))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _service.ValidateTurnaround(day, null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _service.ValidateMealPenalty(null!, TimeSpan.FromHours(7)))).Should().Throw<ArgumentNullException>();
        // Deliberately with the flags OFF. With them on, the guards in ValidateTurnaround
        // throw anyway, so deleting these two changes nothing and the test proves nothing —
        // mutation testing caught exactly that and kept both mutants alive. A null day is a
        // programming error whether or not the rule ends up applying, and the method must
        // say so at the top rather than quietly returning null.
        ((Action)(() => _service.ValidateNightDayTransition(null!, day, false, false))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _service.ValidateNightDayTransition(day, null!, false, false))).Should().Throw<ArgumentNullException>();
    }
}
