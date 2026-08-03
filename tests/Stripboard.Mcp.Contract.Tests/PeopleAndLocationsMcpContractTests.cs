using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Persistence.Seeding;
using Stripboard.Mcp.Locations.Services;
using Stripboard.Mcp.People.Services;
using Xunit;

namespace Stripboard.Mcp.Contract.Tests;

public class PeopleAndLocationsMcpContractTests
{
    [Fact]
    public async Task PeopleMcpService_ExecutesGetPersonGetDoodAndUpdateAvailability_Successfully()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<StripboardDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new StripboardDbContext(options);
        await DataSeeder.SeedAsync(dbContext);

        var service = new PeopleMcpService(dbContext);
        var actor = await dbContext.People.FirstAsync(p => p.Role == PersonRole.Cast);

        // 1. Tool: get_person
        var person = await service.GetPersonAsync(actor.Id);
        person.Should().NotBeNull();
        person!.Name.Should().Be(actor.Name);

        // 2. Tool: get_dood
        var dood = await service.GetDoodAsync(actor.Id, new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 12));
        dood.Should().NotBeNull();
        dood.Days.Should().HaveCount(3);

        // 3. Tool: update_availability
        bool updated = await service.UpdateAvailabilityAsync(actor.Id, new List<DateOnly> { new(2026, 8, 15) });
        updated.Should().BeTrue();
    }

    [Fact]
    public async Task LocationsMcpService_ExecutesGetLocationGetPermitsAndCheckAccess_Successfully()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<StripboardDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new StripboardDbContext(options);
        await DataSeeder.SeedAsync(dbContext);

        var service = new LocationsMcpService(dbContext);

        // 1. Tool: get_location
        var locInfo = await service.GetLocationAsync("221B BAKER STREET");
        locInfo.Should().NotBeNull();
        locInfo!.LocationName.Should().Be("221B BAKER STREET");

        // 2. Tool: get_permits
        var permits = await service.GetPermitsAsync("221B BAKER STREET");
        permits.Should().NotBeEmpty();

        // 3. Tool: check_access
        var accessValid = await service.CheckAccessAsync("221B BAKER STREET", new DateOnly(2026, 8, 15));
        accessValid.HasAccess.Should().BeTrue();

        var accessInvalid = await service.CheckAccessAsync("TOWER BRIDGE WHARF", new DateOnly(2026, 9, 1));
        accessInvalid.HasAccess.Should().BeFalse();
    }
}
