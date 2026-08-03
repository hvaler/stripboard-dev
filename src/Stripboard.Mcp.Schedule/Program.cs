using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Common.Models;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Mcp.Schedule.Services;
using Stripboard.Solver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Register EF Core DbContext
builder.Services.AddDbContext<StripboardDbContext>(options =>
    options.UseInMemoryDatabase("StripboardScheduleMcpDb"));

// Register Solver & Schedule Service
builder.Services.AddScoped<IScheduleSolver, CpSatScheduleSolver>();
builder.Services.AddScoped<ScheduleMcpService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// MCP Tool Endpoints (§6 / ADR-004)
app.MapPost("/mcp/tools/get_schedule", async (GetScheduleRequest request, ScheduleMcpService service) =>
{
    var version = await service.GetScheduleAsync(request.VersionId);
    return version != null ? Results.Ok(version) : Results.NotFound();
});

app.MapPost("/mcp/tools/create_schedule", async (SolverInput input, ScheduleMcpService service) =>
{
    var result = await service.CreateScheduleAsync(input);
    return Results.Ok(result);
});

app.MapPost("/mcp/tools/commit_schedule", async (CommitScheduleRequest request, ScheduleMcpService service) =>
{
    try
    {
        var committedVersion = await service.CommitScheduleAsync(request.ScheduleId, request.ProducerId);
        return Results.Ok(committedVersion);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(ex.Message);
    }
});

app.MapPost("/mcp/tools/validate_rules", async (ValidateRulesRequest request, ScheduleMcpService service) =>
{
    var anomalies = await service.ValidateRulesAsync(request.ScheduleId);
    return Results.Ok(anomalies);
});

app.Run();

public record GetScheduleRequest(Guid VersionId);
public record CommitScheduleRequest(Guid ScheduleId, string ProducerId);
public record ValidateRulesRequest(Guid ScheduleId);
