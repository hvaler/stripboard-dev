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
builder.Services.AddServerSideBlazor();

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

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
