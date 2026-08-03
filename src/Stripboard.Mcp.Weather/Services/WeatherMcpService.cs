namespace Stripboard.Mcp.Weather.Services;

public record WeatherForecastResult(
    string LocationName,
    DateOnly Date,
    double TemperatureCelsius,
    string Condition, // "Sunny", "Cloudy", "Rain", "Heavy Rain", "Fog"
    double PrecipitationProbability,
    double WindSpeedKmh
);

public record WeatherRiskResult(
    string LocationName,
    DateOnly Date,
    bool IsOutdoor,
    string RiskLevel, // "Low", "Medium", "High", "Critical"
    string Recommendation,
    WeatherForecastResult Forecast
);

public class WeatherMcpService
{
    private readonly HttpClient? _httpClient;

    public WeatherMcpService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// MCP Tool: get_forecast(location_name, date)
    /// </summary>
    public Task<WeatherForecastResult> GetForecastAsync(string locationName, DateOnly date, CancellationToken cancellationToken = default)
    {
        // Deterministic forecast stub based on location and date hash for test/demo reproducibility (§6 / ADR-004)
        var condition = "Sunny";
        double precipProb = 10.0;
        double temp = 22.0;
        double wind = 12.0;

        if (locationName.Contains("WHARF", StringComparison.OrdinalIgnoreCase) || locationName.Contains("RIVER", StringComparison.OrdinalIgnoreCase))
        {
            condition = "Fog";
            precipProb = 45.0;
            temp = 16.0;
            wind = 25.0;
        }
        else if (date.Day % 5 == 0)
        {
            condition = "Rain";
            precipProb = 85.0;
            temp = 15.0;
            wind = 35.0;
        }

        var forecast = new WeatherForecastResult(locationName, date, temp, condition, precipProb, wind);
        return Task.FromResult(forecast);
    }

    /// <summary>
    /// MCP Tool: check_risk(location_name, date, is_outdoor)
    /// </summary>
    public async Task<WeatherRiskResult> CheckRiskAsync(string locationName, DateOnly date, bool isOutdoor, CancellationToken cancellationToken = default)
    {
        var forecast = await GetForecastAsync(locationName, date, cancellationToken);

        string riskLevel = "Low";
        string recommendation = "Conditions optimal for shooting.";

        if (!isOutdoor)
        {
            riskLevel = "Low";
            recommendation = "Indoor shooting unaffected by weather.";
        }
        else if (forecast.Condition == "Rain" || forecast.PrecipitationProbability > 70.0)
        {
            riskLevel = "High";
            recommendation = "High rain probability. Prepare rain cover or reschedule outdoor scenes.";
        }
        else if (forecast.Condition == "Fog" || forecast.WindSpeedKmh > 20.0)
        {
            riskLevel = "Medium";
            recommendation = "Moderate weather conditions. Monitor fog/wind impact on camera and drone setups.";
        }

        return new WeatherRiskResult(locationName, date, isOutdoor, riskLevel, recommendation, forecast);
    }
}
