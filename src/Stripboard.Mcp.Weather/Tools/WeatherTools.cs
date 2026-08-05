using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Stripboard.Mcp.Weather.Services;

namespace Stripboard.Mcp.Weather.Tools;

/// <summary>
/// Weather risk for exterior shooting days, over the Model Context Protocol (EV-23).
///
/// **The forecast behind these tools is synthetic.** It is a deterministic function of the
/// location name and the date, so the demo is reproducible — no meteorological service is
/// called. Every result therefore carries `source: "synthetic"`, and the tool descriptions
/// say so, because an agent handed a number will otherwise present it to a producer as
/// tomorrow's weather. Swapping in a real forecast API is tracked as future work; until then
/// the honest thing is to label it on every response rather than in a comment nobody reads.
/// </summary>
[McpServerToolType]
public sealed class WeatherTools
{
    private const string Source = "synthetic";
    private const string Caveat =
        "SYNTHETIC forecast, derived from the location name and date for reproducible demos. "
        + "Not a meteorological prediction — do not present it as one.";

    private readonly WeatherMcpService _weather;

    public WeatherTools(WeatherMcpService weather)
        => _weather = weather ?? throw new ArgumentNullException(nameof(weather));

    [McpServerTool(Name = "get_forecast")]
    [Description("Weather for a location on a date: condition, temperature, rain probability and "
               + "wind. SYNTHETIC — generated deterministically for demo reproducibility, not "
               + "fetched from any weather service.")]
    public async Task<object> GetForecastAsync(
        [Description("Location name, e.g. 'TOWER BRIDGE WHARF'.")] string locationName,
        [Description("The date, ISO format (YYYY-MM-DD).")] string date,
        CancellationToken ct = default)
    {
        var forecast = await _weather.GetForecastAsync(locationName, ParseDate(date), ct);

        return new
        {
            forecast.LocationName,
            date = forecast.Date.ToString("yyyy-MM-dd"),
            forecast.Condition,
            forecast.TemperatureCelsius,
            forecast.PrecipitationProbability,
            forecast.WindSpeedKmh,
            source = Source,
            caveat = Caveat,
        };
    }

    [McpServerTool(Name = "check_risk")]
    [Description("Whether weather threatens a shooting day, and what to do about it. Interiors "
               + "carry little risk; exteriors carry the day. SYNTHETIC — see get_forecast.")]
    public async Task<object> CheckRiskAsync(
        [Description("Location name.")] string locationName,
        [Description("The date, ISO format (YYYY-MM-DD).")] string date,
        [Description("True when the scenes that day are exterior. Interiors are largely weatherproof.")]
        bool isOutdoor,
        CancellationToken ct = default)
    {
        var risk = await _weather.CheckRiskAsync(locationName, ParseDate(date), isOutdoor, ct);

        return new
        {
            risk.LocationName,
            date = risk.Date.ToString("yyyy-MM-dd"),
            risk.IsOutdoor,
            risk.RiskLevel,
            risk.Recommendation,
            forecast = new
            {
                risk.Forecast.Condition,
                risk.Forecast.PrecipitationProbability,
                risk.Forecast.WindSpeedKmh,
            },
            source = Source,
            caveat = Caveat,
        };
    }

    private static DateOnly ParseDate(string value) => IsoDate.Parse(value);
}
