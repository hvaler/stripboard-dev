using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Services;
using Stripboard.Infrastructure.Persistence;
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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StripboardDbContext>();
    await DatabaseRegistration.MigrateAsync(db, app.Logger);
}

app.MapMcp("/mcp");

app.Run();
