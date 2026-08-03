using Microsoft.EntityFrameworkCore;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Mcp.Locations.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<StripboardDbContext>(options =>
    options.UseInMemoryDatabase("StripboardLocationsMcpDb"));
builder.Services.AddScoped<LocationsMcpService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// MCP Tool Endpoints for mcp-locations (§6 / ADR-004)
app.MapPost("/mcp/tools/get_location", async (GetLocationRequest request, LocationsMcpService service) =>
{
    var info = await service.GetLocationAsync(request.LocationName);
    return info != null ? Results.Ok(info) : Results.NotFound();
});

app.MapPost("/mcp/tools/get_permits", async (GetPermitsRequest request, LocationsMcpService service) =>
{
    var permits = await service.GetPermitsAsync(request.LocationName);
    return Results.Ok(permits);
});

app.MapPost("/mcp/tools/check_access", async (CheckAccessRequest request, LocationsMcpService service) =>
{
    var access = await service.CheckAccessAsync(request.LocationName, request.Date);
    return Results.Ok(access);
});

app.Run();

public record GetLocationRequest(string LocationName);
public record GetPermitsRequest(string LocationName);
public record CheckAccessRequest(string LocationName, DateOnly Date);
