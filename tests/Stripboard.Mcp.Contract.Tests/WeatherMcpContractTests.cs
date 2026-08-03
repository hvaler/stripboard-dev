using FluentAssertions;
using Stripboard.Mcp.Weather.Services;
using Xunit;

namespace Stripboard.Mcp.Contract.Tests;

public class WeatherMcpContractTests
{
    [Fact]
    public async Task WeatherMcpService_ExecutesGetForecastAndCheckRisk_Successfully()
    {
        // Arrange
        var service = new WeatherMcpService();

        // 1. Tool: get_forecast
        var forecast = await service.GetForecastAsync("221B BAKER STREET", new DateOnly(2026, 8, 10));
        forecast.Should().NotBeNull();
        forecast.LocationName.Should().Be("221B BAKER STREET");

        // 2. Tool: check_risk (Indoor)
        var indoorRisk = await service.CheckRiskAsync("221B BAKER STREET", new DateOnly(2026, 8, 10), isOutdoor: false);
        indoorRisk.RiskLevel.Should().Be("Low");

        // 3. Tool: check_risk (Outdoor with Fog/Wharf)
        var outdoorRisk = await service.CheckRiskAsync("TOWER BRIDGE WHARF", new DateOnly(2026, 8, 10), isOutdoor: true);
        outdoorRisk.RiskLevel.Should().Be("Medium");
    }
}
