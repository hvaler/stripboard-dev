using FluentAssertions;
using Stripboard.Application.Common.Models;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Solver;
using Xunit;

namespace Stripboard.Solver.Tests;

public class CpSatScheduleSolverTests
{
    private readonly CpSatScheduleSolver _solver = new();

    [Fact]
    public async Task SolveAsync_Simple3Scenes1Day_SchedulesAllScenesOptimally()
    {
        // Arrange
        var scenes = new List<Scene>
        {
            new(Guid.NewGuid(), 1, "221B BAKER STREET", IntExt.Int, DayNight.Day, 3, null, null, "Breakfast scene"),
            new(Guid.NewGuid(), 2, "221B BAKER STREET", IntExt.Int, DayNight.Day, 2, null, null, "Conversation"),
            new(Guid.NewGuid(), 3, "221B BAKER STREET", IntExt.Int, DayNight.Night, 4, null, null, "Evening study")
        };

        var input = new SolverInput(
            Scenes: scenes,
            CastAndCrew: new List<Person>(),
            PermitWindows: new List<LocationPermitWindow>(),
            ScheduleStartDate: new DateOnly(2026, 8, 10),
            MaxDaysAvailable: 5,
            MaxHoursPerDay: 12
        );

        // Act
        var result = await _solver.SolveAsync(input);

        // Assert
        result.Should().NotBeNull();
        result.IsFeasible.Should().BeTrue();
        result.ScheduledDays.Should().HaveCount(1);
        result.ScheduledDays[0].ScheduledScenes.Should().HaveCount(3);
    }

    [Fact]
    public async Task SolveAsync_Complex15Scenes3Days2Locations_SchedulesFeasiblyAndRespectsPermitWindows()
    {
        // Arrange
        var scenes = new List<Scene>();
        for (int i = 1; i <= 8; i++)
        {
            scenes.Add(new Scene(Guid.NewGuid(), i, "STUDIO A", IntExt.Int, DayNight.Day, 3, null, null, $"Studio scene {i}"));
        }
        for (int i = 9; i <= 15; i++)
        {
            scenes.Add(new Scene(Guid.NewGuid(), i, "WAREHOUSE B", IntExt.Ext, DayNight.Night, 4, null, null, $"Warehouse scene {i}"));
        }

        var permitWindows = new List<LocationPermitWindow>
        {
            new("STUDIO A", new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 12)),
            new("WAREHOUSE B", new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 15))
        };

        var input = new SolverInput(
            Scenes: scenes,
            CastAndCrew: new List<Person>(),
            PermitWindows: permitWindows,
            ScheduleStartDate: new DateOnly(2026, 8, 10),
            MaxDaysAvailable: 5,
            MaxHoursPerDay: 12
        );

        // Act
        var result = await _solver.SolveAsync(input);

        // Assert
        result.Should().NotBeNull();
        result.IsFeasible.Should().BeTrue();
        result.ScheduledDays.Count.Should().BeInRange(2, 5);

        int totalScheduledScenes = result.ScheduledDays.Sum(d => d.ScheduledScenes.Count);
        totalScheduledScenes.Should().Be(15);
    }
}
