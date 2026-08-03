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
}
