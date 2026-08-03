using Stripboard.Mcp.Weather.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddScoped<WeatherMcpService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// MCP Tool Endpoints for mcp-weather (§6 / ADR-004)
app.MapPost("/mcp/tools/get_forecast", async (GetForecastRequest request, WeatherMcpService service) =>
{
    var forecast = await service.GetForecastAsync(request.LocationName, request.Date);
    return Results.Ok(forecast);
});

app.MapPost("/mcp/tools/check_risk", async (CheckRiskRequest request, WeatherMcpService service) =>
{
    var risk = await service.CheckRiskAsync(request.LocationName, request.Date, request.IsOutdoor);
    return Results.Ok(risk);
});

app.Run();

public record GetForecastRequest(string LocationName, DateOnly Date);
public record CheckRiskRequest(string LocationName, DateOnly Date, bool IsOutdoor);
