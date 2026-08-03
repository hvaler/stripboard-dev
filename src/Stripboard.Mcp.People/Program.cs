using Microsoft.EntityFrameworkCore;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Mcp.People.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<StripboardDbContext>(options =>
    options.UseInMemoryDatabase("StripboardPeopleMcpDb"));
builder.Services.AddScoped<PeopleMcpService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// MCP Tool Endpoints for mcp-people (§6 / ADR-004)
app.MapPost("/mcp/tools/get_person", async (GetPersonRequest request, PeopleMcpService service) =>
{
    var person = await service.GetPersonAsync(request.PersonId);
    return person != null ? Results.Ok(person) : Results.NotFound();
});

app.MapPost("/mcp/tools/get_dood", async (GetDoodRequest request, PeopleMcpService service) =>
{
    var dood = await service.GetDoodAsync(request.PersonId, request.StartDate, request.EndDate);
    return Results.Ok(dood);
});

app.MapPost("/mcp/tools/update_availability", async (UpdateAvailabilityRequest request, PeopleMcpService service) =>
{
    var success = await service.UpdateAvailabilityAsync(request.PersonId, request.UnavailableDates);
    return success ? Results.Ok(new { success = true }) : Results.NotFound();
});

app.Run();

public record GetPersonRequest(Guid PersonId);
public record GetDoodRequest(Guid PersonId, DateOnly StartDate, DateOnly EndDate);
public record UpdateAvailabilityRequest(Guid PersonId, List<DateOnly> UnavailableDates);
