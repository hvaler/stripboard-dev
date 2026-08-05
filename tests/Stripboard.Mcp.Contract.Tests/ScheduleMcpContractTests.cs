using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Services;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Services;
using Stripboard.Mcp.Schedule.Tools;
using Stripboard.Solver;

namespace Stripboard.Mcp.Contract.Tests;

/// <summary>
/// mcp-schedule, driven through the actual protocol (EV-23).
///
/// The test this replaces called the service class directly and asserted that committing as
/// <c>"producer-hugo"</c> succeeded — which it did, because the server had no authorisation
/// check at all. A test written against the implementation blessed the hole it should have
/// caught. These go through <c>tools/call</c>, where an agent would.
/// </summary>
public class ScheduleMcpContractTests
{
    private static readonly Guid Holmes = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <param name="authenticatedAs">
    /// The identity the platform proves, or null for a caller nothing verified — which is
    /// what an agent hitting this server without a Cloud Run identity token looks like.
    /// </param>
    private static Task<McpTestServer> StartAsync(string databaseName, string? authenticatedAs = null) =>
        McpTestServer.StartAsync<ScheduleTools>(services =>
        {
            services.AddDbContext<StripboardDbContext>(o => o.UseInMemoryDatabase(databaseName));
            services.AddScoped<IScheduleSolver, CpSatScheduleSolver>();
            services.AddScoped<AgentAuthorizationService>();
            services.AddScoped<ScheduleService>();
            services.AddScoped<ReplanService>();
            services.AddSingleton(McpTestServer.ResolverFor(authenticatedAs));
        });

    private static void Seed(string databaseName)
    {
        var options = new DbContextOptionsBuilder<StripboardDbContext>()
            .UseInMemoryDatabase(databaseName).Options;
        using var db = new StripboardDbContext(options);

        db.People.AddRange(
            new Person(Holmes, "Sherlock Holmes", PersonRole.Cast, 1500m),
            new Person(Guid.NewGuid(), "1st AD", PersonRole.FirstAssistantDirector, 900m));

        for (var i = 1; i <= 6; i++)
        {
            db.Scenes.Add(new Scene(Guid.NewGuid(), i,
                i <= 3 ? "221B BAKER STREET" : "TOWER BRIDGE", IntExt.Int, DayNight.Day,
                8, [Holmes], null, $"Scene {i}"));
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task TheServerSpeaksMcp_AndAdvertisesItsToolsWithUsableSchemas()
    {
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name);

        var tools = await mcp.ListToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo(
            "get_schedule", "create_schedule", "commit_schedule", "validate_rules", "consolidate_schedule");

        foreach (var tool in tools)
        {
            tool.Description.Should().NotBeNullOrWhiteSpace(
                $"{tool.Name} is discovered by agents through this description alone");
            tool.JsonSchema.GetProperty("type").GetString().Should().Be("object");
        }
    }

    [Fact]
    public async Task CreateScheduleTakesTheChoicesAProducerMakes_NotADomainObject()
    {
        // The old server's create_schedule accepted a whole SolverInput — lists of scenes and
        // people nested inside the argument. That is a legal MCP schema and an unusable one:
        // an agent asked to fill it will invent the contents of the production.
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name);

        var schema = (await mcp.ListToolsAsync()).Single(t => t.Name == "create_schedule").JsonSchema;
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        properties.Should().BeEquivalentTo(
            "identity", "startDate", "maxDaysAvailable", "maxLocationsPerDay");
        foreach (var property in properties)
        {
            var kind = schema.GetProperty("properties").GetProperty(property);
            kind.TryGetProperty("properties", out _).Should().BeFalse(
                $"{property} must be a scalar an agent can actually supply");
        }
    }

    [Fact]
    public async Task TheFullFlowRunsOverTheProtocol()
    {
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name, authenticatedAs: "producer@stripboard.example");

        var created = await mcp.CallAsync("create_schedule",
            new { identity = "Producer", startDate = "2026-08-10" });
        created.GetProperty("days").GetInt32().Should().BeGreaterThan(0);
        created.GetProperty("isCommitted").GetBoolean().Should().BeFalse("create must not commit");

        var versionId = created.GetProperty("versionId").GetGuid();

        var read = await mcp.CallAsync("get_schedule");
        read.GetProperty("versionId").GetGuid().Should().Be(versionId);

        var validated = await mcp.CallAsync("validate_rules");
        validated.GetProperty("clean").GetBoolean().Should().BeTrue(
            "the solver builds schedules that satisfy the union rules by construction");
    }

    [Fact]
    public async Task AProvenProducerCanCommitOverMcp()
    {
        var name = Guid.NewGuid().ToString();
        Seed(name);
        // "Producer" as the platform-proved principal, the shape a human arrives in behind IAP.
        await using var mcp = await StartAsync(name, authenticatedAs: "Producer");

        var created = await mcp.CallAsync("create_schedule",
            new { identity = "Producer", startDate = "2026-08-10" });

        var committed = await mcp.CallAsync("commit_schedule",
            new { versionId = created.GetProperty("versionId").GetGuid() });

        committed.GetProperty("committed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AnAgentCallingCommitOverMcpIsRefused()
    {
        // The governance rule, exercised at the boundary an agent actually uses.
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name,
            authenticatedAs: "sa-replanner@stripboard-hack.iam.gserviceaccount.com");

        var created = await mcp.CallAsync("create_schedule",
            new { identity = "sa-replanner", startDate = "2026-08-10" });
        var versionId = created.GetProperty("versionId").GetGuid();

        var act = () => mcp.CallAsync("commit_schedule", new { versionId });

        (await act.Should().ThrowAsync<McpException>())
            .WithMessage("*Only the Producer role may commit*");
    }

    [Fact]
    public async Task AnAgentThatSimplyClaimsToBeTheProducerIsRefused()
    {
        // The hole this closes. The identity used to be a string in the request body, so an
        // agent that did not want to be refused only had to send a different one.
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name,
            authenticatedAs: "sa-replanner@stripboard-hack.iam.gserviceaccount.com");

        var created = await mcp.CallAsync("create_schedule",
            new { identity = "sa-replanner", startDate = "2026-08-10" });

        var act = () => mcp.CallAsync("commit_schedule", new
        {
            versionId = created.GetProperty("versionId").GetGuid(),
            identity = "Producer",
        });

        (await act.Should().ThrowAsync<McpException>())
            .WithMessage("*Only the Producer role may commit*");
    }

    [Fact]
    public async Task WithNothingAuthenticatingTheCaller_NobodyCanCommit()
    {
        // Running with no platform in front — a laptop, a misconfigured deployment — every
        // caller is unverified, and unverified cannot commit. That is the safe direction to
        // fail in.
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name, authenticatedAs: null);

        var created = await mcp.CallAsync("create_schedule",
            new { identity = "Producer", startDate = "2026-08-10" });

        var act = () => mcp.CallAsync("commit_schedule", new
        {
            versionId = created.GetProperty("versionId").GetGuid(),
            identity = "Producer",
        });

        (await act.Should().ThrowAsync<McpException>())
            .WithMessage("*claims the Producer role but nothing verified it*");
    }

    [Fact]
    public async Task AnIdentityThatCannotSolveIsRefused()
    {
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name);

        var act = () => mcp.CallAsync("create_schedule",
            new { identity = "sa-sentinel", startDate = "2026-08-10" });

        (await act.Should().ThrowAsync<McpException>())
            .WithMessage("*not permitted to run the solver*");
    }

    [Fact]
    public async Task AskingForASchedulThatDoesNotExistSaysSo_RatherThanReturningAnEmptyOne()
    {
        // An empty board would read as "a shoot with no days", which is a different and much
        // worse answer than "nothing has been scheduled".
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name);

        var act = () => mcp.CallAsync("get_schedule");

        (await act.Should().ThrowAsync<McpException>())
            .WithMessage("*No schedule has been committed yet*");
    }

    [Fact]
    public async Task ABadDateIsRejectedWithSomethingTheCallerCanActOn()
    {
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name);

        var act = () => mcp.CallAsync("create_schedule",
            new { identity = "Producer", startDate = "next Tuesday" });

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*YYYY-MM-DD*");
    }

    [Fact]
    public async Task ValidateRulesUsesTheVersionItIsGiven()
    {
        // The old implementation ignored its schedule_id and validated every ShootDay in the
        // database. It answered a question nobody asked, and answered it confidently.
        var name = Guid.NewGuid().ToString();
        Seed(name);
        await using var mcp = await StartAsync(name);

        await mcp.CallAsync("create_schedule", new { identity = "Producer", startDate = "2026-08-10" });

        var act = () => mcp.CallAsync("validate_rules", new { versionId = Guid.NewGuid() });

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*does not exist*");
    }
}
