using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Models;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Mcp.Schedule.Services;
using Stripboard.Solver;

namespace Stripboard.Mcp.Contract.Tests;

public class ScheduleMcpContractTests
{
    [Fact]
    public async Task ScheduleMcpService_ExecutesCreateGetCommitAndValidateFlow_Successfully()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<StripboardDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new StripboardDbContext(options);
        var solver = new CpSatScheduleSolver();
        var service = new ScheduleMcpService(dbContext, solver);

        var scenes = new List<Scene>
        {
            new(Guid.NewGuid(), 1, "BAKER STREET", IntExt.Int, DayNight.Day, 4, null, null, "Breakfast scene"),
            new(Guid.NewGuid(), 2, "TOWER BRIDGE", IntExt.Ext, DayNight.Night, 4, null, null, "Wharf scene")
        };

        var input = new SolverInput(
            Scenes: scenes,
            CastAndCrew: new List<Person>(),
            PermitWindows: new List<LocationPermitWindow>(),
            ScheduleStartDate: new DateOnly(2026, 8, 10),
            MaxDaysAvailable: 3,
            MaxHoursPerDay: 12
        );

        // 1. Tool: create_schedule
        var createResult = await service.CreateScheduleAsync(input);
        createResult.Should().NotBeNull();
        createResult.IsFeasible.Should().BeTrue();

        var version = await dbContext.ScheduleVersions.FirstOrDefaultAsync();
        version.Should().NotBeNull();
        version!.IsCommitted.Should().BeFalse();

        // 2. Tool: get_schedule
        var fetchedVersion = await service.GetScheduleAsync(version.Id);
        fetchedVersion.Should().NotBeNull();
        fetchedVersion!.Id.Should().Be(version.Id);

        // 3. Tool: commit_schedule (Human-in-the-Loop approval)
        var committedVersion = await service.CommitScheduleAsync(version.Id, producerId: "producer-hugo");
        committedVersion.IsCommitted.Should().BeTrue();

        // 4. Tool: validate_rules
        var anomalies = await service.ValidateRulesAsync(version.Id);
        anomalies.Should().NotBeNull();
    }
}
