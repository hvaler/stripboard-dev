using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Services;
using Stripboard.CallSheets.Services;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Persistence.Seeding;
using Stripboard.Infrastructure.Services;
using Stripboard.Mcp.Schedule.Services;
using Stripboard.Solver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

// Blazor Server keeps a stateful SignalR circuit per user. On Cloud Run that only works
// if the instance stays alive and the client keeps reaching the same one, hence the
// deployment flags in infra/deploy-web.sh. Retaining disconnected circuits lets a brief
// network blip reconnect instead of dropping the user into "Rejoining the server…".
builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(options =>
    {
        options.DetailedErrors = builder.Environment.IsDevelopment();
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(5);
        options.DisconnectedCircuitMaxRetained = 100;
    });

// Cloud Run terminates TLS and forwards plain HTTP. Without this the app believes every
// request is insecure and UseHttpsRedirection bounces it, which breaks the WebSocket
// upgrade the circuit depends on.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddDbContext<StripboardDbContext>(options =>
    options.UseInMemoryDatabase("StripboardWebDb"));

// The solver is what makes the UI real (EV-21). Before this registration the web app
// resolved ScheduleMcpService without ever providing an IScheduleSolver to satisfy it.
builder.Services.AddScoped<IScheduleSolver, CpSatScheduleSolver>();
builder.Services.AddScoped<AgentAuthorizationService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<ReplanService>();
builder.Services.AddScoped<BreakdownImportService>();
builder.Services.AddScoped<ScheduleMcpService>();
builder.Services.AddSingleton<CallSheetPdfGenerator>();

var app = builder.Build();

// Seed the demo screenplay and solve an initial schedule, so the board has something real
// to render on first load rather than placeholder markup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StripboardDbContext>();
    await DataSeeder.SeedAsync(db);

    if (!await db.ShootDays.AnyAsync(d => d.ScheduleVersionId != null))
    {
        var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();
        try
        {
            await schedules.GenerateAsync(
                createdBy: AgentAuthorizationService.RoleProducer,
                startDate: new DateOnly(2026, 8, 10),
                commit: true);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Could not generate the initial schedule at startup.");
        }
    }
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

// Readiness probe. Reports whether the app actually has a schedule to show, not merely
// whether the process is up — a running instance with an empty board is not ready to demo.
//
// Deliberately NOT /healthz: Google's frontend intercepts that exact path on *.run.app
// domains and answers 404 itself, so the request never reaches the container. It works
// locally, which makes it a genuinely confusing thing to debug in production.
app.MapGet("/api/health", async (ScheduleService schedules, CancellationToken ct) =>
{
    var board = await schedules.GetActiveBoardAsync(ct);
    return board is null
        ? Results.Json(new { status = "degraded", reason = "no schedule version" }, statusCode: 503)
        : Results.Ok(new
        {
            status = "ok",
            board.VersionNumber,
            board.IsCommitted,
            days = board.Metrics.TotalDays,
            scenes = board.Days.Sum(d => d.Scenes.Count),
        });
});

// Import a breakdown produced by the Gemini agent and immediately re-solve, so a new
// screenplay is visible on the stripboard:
//   python -m agents.breakdown --file demo/screenplay-harbour.fountain --json \
//     | curl -X POST http://localhost:5164/api/breakdown/import -H 'Content-Type: application/json' --data-binary @-
app.MapPost("/api/breakdown/import", async (
    HttpRequest request,
    BreakdownImportService importer,
    ScheduleService schedules,
    CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var json = await reader.ReadToEndAsync(ct);

    try
    {
        var imported = await importer.ImportAsync(json, ct);
        var board = await schedules.GenerateAsync(
            createdBy: AgentAuthorizationService.RoleProducer,
            startDate: new DateOnly(2026, 8, 10),
            commit: true,
            ct: ct);

        return Results.Ok(new
        {
            imported.Scenes,
            imported.CastCreated,
            imported.Source,
            board.VersionNumber,
            board.Metrics.TotalDays,
            board.Metrics.CompanyMoves,
            board.Metrics.EstimatedCostUsd,
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
