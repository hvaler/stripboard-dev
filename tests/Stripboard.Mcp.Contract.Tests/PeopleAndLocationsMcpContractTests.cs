using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Stripboard.Domain.Enums;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Persistence.Seeding;
using Stripboard.Mcp.Locations.Services;
using Stripboard.Mcp.Locations.Tools;
using Stripboard.Mcp.People.Services;
using Stripboard.Mcp.People.Tools;
using Xunit;

namespace Stripboard.Mcp.Contract.Tests;

/// <summary>mcp-people and mcp-locations, driven through the protocol (EV-23).</summary>
public class PeopleAndLocationsMcpContractTests
{
    private static async Task<string> SeededDatabaseAsync()
    {
        var name = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<StripboardDbContext>()
            .UseInMemoryDatabase(name).Options;
        await using var db = new StripboardDbContext(options);
        await DataSeeder.SeedAsync(db);
        return name;
    }

    private static async Task<Guid> FirstCastIdAsync(string databaseName)
    {
        var options = new DbContextOptionsBuilder<StripboardDbContext>()
            .UseInMemoryDatabase(databaseName).Options;
        await using var db = new StripboardDbContext(options);
        return (await db.People.FirstAsync(p => p.Role == PersonRole.Cast)).Id;
    }

    // ── mcp-people ─────────────────────────────────────────────────────────────

    private static Task<McpTestServer> StartPeopleAsync(string databaseName) =>
        McpTestServer.StartAsync<PeopleTools>(services =>
        {
            services.AddDbContext<StripboardDbContext>(o => o.UseInMemoryDatabase(databaseName));
            services.AddScoped<PeopleMcpService>();
        });

    [Fact]
    public async Task PeopleServerAdvertisesItsTools()
    {
        await using var mcp = await StartPeopleAsync(await SeededDatabaseAsync());

        var tools = await mcp.ListToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo("get_person", "get_dood", "update_availability");
    }

    [Fact]
    public async Task GetPersonAndDoodComeBackOverTheProtocol()
    {
        var database = await SeededDatabaseAsync();
        var actorId = await FirstCastIdAsync(database);
        await using var mcp = await StartPeopleAsync(database);

        var person = await mcp.CallAsync("get_person", new { personId = actorId });
        person.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
        person.GetProperty("isCast").GetBoolean().Should().BeTrue();

        var dood = await mcp.CallAsync("get_dood", new
        {
            personId = actorId,
            startDate = "2026-08-10",
            endDate = "2026-08-12",
        });
        dood.GetProperty("days").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task UpdatingAvailabilityForSomebodyWhoDoesNotExistIsAnError_NotASilentNoOp()
    {
        // The service answers false here. Passing that back as an ordinary result would let a
        // caller believe an actor's unavailability had been recorded when nothing happened.
        await using var mcp = await StartPeopleAsync(await SeededDatabaseAsync());

        var act = () => mcp.CallAsync("update_availability", new
        {
            personId = Guid.NewGuid(),
            unavailableDates = new[] { "2026-08-15" },
        });

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*nothing was recorded*");
    }

    [Fact]
    public async Task AnUnparseableDateIsRefusedWithTheFormatItWanted()
    {
        var database = await SeededDatabaseAsync();
        var actorId = await FirstCastIdAsync(database);
        await using var mcp = await StartPeopleAsync(database);

        var act = () => mcp.CallAsync("get_dood", new
        {
            personId = actorId,
            startDate = "10/08/2026",
            endDate = "2026-08-12",
        });

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*YYYY-MM-DD*");
    }

    // ── mcp-locations ──────────────────────────────────────────────────────────

    private static Task<McpTestServer> StartLocationsAsync(string databaseName) =>
        McpTestServer.StartAsync<LocationsTools>(services =>
        {
            services.AddDbContext<StripboardDbContext>(o => o.UseInMemoryDatabase(databaseName));
            services.AddScoped<LocationsMcpService>();
        });

    [Fact]
    public async Task LocationsServerAdvertisesItsTools()
    {
        await using var mcp = await StartLocationsAsync(await SeededDatabaseAsync());

        var tools = await mcp.ListToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo("get_location", "get_permits", "check_access");
    }

    [Fact]
    public async Task PermitsAndAccessComeBackOverTheProtocol()
    {
        await using var mcp = await StartLocationsAsync(await SeededDatabaseAsync());

        var permits = await mcp.CallAsync("get_permits", new { locationName = "221B BAKER STREET" });
        permits.GetProperty("permitted").GetBoolean().Should().BeTrue();
        permits.GetProperty("windows").GetArrayLength().Should().BeGreaterThan(0);

        var allowed = await mcp.CallAsync("check_access",
            new { locationName = "221B BAKER STREET", date = "2026-08-15" });
        allowed.GetProperty("hasAccess").GetBoolean().Should().BeTrue();

        var refused = await mcp.CallAsync("check_access",
            new { locationName = "TOWER BRIDGE WHARF", date = "2026-09-01" });
        refused.GetProperty("hasAccess").GetBoolean().Should().BeFalse();
        refused.GetProperty("details").GetString().Should().NotBeNullOrWhiteSpace(
            "a refusal has to say why, or a producer cannot act on it");
    }

    [Fact]
    public async Task AnUnknownLocationIsAnError_NotAnEmptyLocation()
    {
        await using var mcp = await StartLocationsAsync(await SeededDatabaseAsync());

        var act = () => mcp.CallAsync("get_location", new { locationName = "ATLANTIS" });

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*No location named*");
    }
}
