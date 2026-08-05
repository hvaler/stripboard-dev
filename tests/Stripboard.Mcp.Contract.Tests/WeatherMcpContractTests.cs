using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Stripboard.Mcp.Weather.Services;
using Stripboard.Mcp.Weather.Tools;
using Xunit;

namespace Stripboard.Mcp.Contract.Tests;

/// <summary>mcp-weather, driven through the protocol (EV-23).</summary>
public class WeatherMcpContractTests
{
    private static Task<McpTestServer> StartAsync() =>
        McpTestServer.StartAsync<WeatherTools>(services => services.AddScoped<WeatherMcpService>());

    [Fact]
    public async Task TheServerAdvertisesItsTools()
    {
        await using var mcp = await StartAsync();

        var tools = await mcp.ListToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo("get_forecast", "check_risk");
    }

    [Fact]
    public async Task EveryForecastSaysItIsSynthetic()
    {
        // This is the point of the whole file. The forecast is a deterministic function of the
        // location name and the date — no weather service is called — and an agent handed a
        // number with no provenance will present it to a producer as tomorrow's weather.
        await using var mcp = await StartAsync();

        var forecast = await mcp.CallAsync("get_forecast",
            new { locationName = "221B BAKER STREET", date = "2026-08-10" });
        forecast.GetProperty("source").GetString().Should().Be("synthetic");
        forecast.GetProperty("caveat").GetString().Should().Contain("SYNTHETIC");

        var risk = await mcp.CallAsync("check_risk",
            new { locationName = "TOWER BRIDGE WHARF", date = "2026-08-10", isOutdoor = true });
        risk.GetProperty("source").GetString().Should().Be("synthetic");
    }

    [Fact]
    public async Task TheToolDescriptionsWarnBeforeTheResultDoes()
    {
        // A model chooses a tool from its description and may never look at the payload's
        // provenance field. The warning has to be where the choice is made.
        await using var mcp = await StartAsync();

        foreach (var tool in await mcp.ListToolsAsync())
        {
            tool.Description.Should().Contain("SYNTHETIC", $"{tool.Name} is chosen by its description");
        }
    }

    [Fact]
    public async Task RiskDependsOnWhetherTheDayIsExterior()
    {
        await using var mcp = await StartAsync();

        var indoors = await mcp.CallAsync("check_risk",
            new { locationName = "221B BAKER STREET", date = "2026-08-10", isOutdoor = false });
        indoors.GetProperty("riskLevel").GetString().Should().Be("Low");

        var outdoors = await mcp.CallAsync("check_risk",
            new { locationName = "TOWER BRIDGE WHARF", date = "2026-08-10", isOutdoor = true });
        outdoors.GetProperty("riskLevel").GetString().Should().Be("Medium");
        outdoors.GetProperty("recommendation").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ABadDateIsRefusedWithTheFormatItWanted()
    {
        await using var mcp = await StartAsync();

        var act = () => mcp.CallAsync("get_forecast",
            new { locationName = "ORDSALL PARK", date = "tomorrow" });

        (await act.Should().ThrowAsync<McpException>()).WithMessage("*YYYY-MM-DD*");
    }
}
