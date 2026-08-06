using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Services;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Persistence.Seeding;
using Stripboard.Infrastructure.Services;
using Stripboard.Mcp.Schedule.Tools;
using Stripboard.Solver;

// mcp-schedule: a real Model Context Protocol server (EV-23).
//
// This used to be four REST endpoints under an /mcp/ path, which is not the same thing as
// speaking MCP — there was no initialize handshake, no tools/list, no typed input schemas,
// and no client could discover it. ADR-001 claimed "the Python/.NET boundary is the MCP
// boundary"; until now that was only true in one direction, because we were a client of
// Grafana's server and not a server ourselves.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStripboardDatabase(builder.Configuration, "StripboardScheduleMcpDb");
builder.Services.AddScoped<IScheduleSolver, CpSatScheduleSolver>();
builder.Services.AddScoped<AgentAuthorizationService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<ReplanService>();

// Who is calling comes from the request, not from the request body. Behind Cloud Run that
// is the identity token Google already validated; locally it is nobody, and nobody cannot
// commit. See CallerIdentityResolver.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CallerIdentityResolver>();

builder.Services.AddMcpServer()
    // Stateless: these tools are request/response over the database and never call back to
    // the client for sampling or elicitation, so there is no session state worth keeping —
    // and a stateless server survives Cloud Run moving a request to another instance.
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<ScheduleTools>();

var app = builder.Build();

// Migrate, then seed, exactly as the web app does. Seeding matters more here than it looks:
// with no connection string this server gets its own in-memory database, so without a seed a
// client completes the handshake, discovers five tools, calls one, and is told there is
// nothing to schedule. The protocol works and the server appears empty — which reads as a
// broken integration rather than as an unseeded database. DataSeeder is idempotent, so
// against a shared Cloud SQL instance this is a no-op.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StripboardDbContext>();
    await DatabaseRegistration.MigrateAsync(db, app.Logger);
    await DataSeeder.SeedAsync(db);

    // And solve one, if nobody has. Same guard as the web app: a schedule already in the
    // database is left exactly as it is, so pointing this server at Cloud SQL alongside the
    // web app does not produce a second, competing plan.
    var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();
    if (!await db.ShootDays.AnyAsync(d => d.ScheduleVersionId != null))
    {
        await schedules.GenerateAsync(
            createdBy: AgentAuthorizationService.RoleProducer,
            startDate: new DateOnly(2026, 8, 10),
            commit: true);
        app.Logger.LogInformation("Solved and committed an initial schedule for MCP clients.");
    }
}

app.MapMcp("/mcp");

app.Run();
