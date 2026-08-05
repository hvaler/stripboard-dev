using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Interfaces;
using Stripboard.Application.Common.Models;
using Stripboard.Application.Services;
using Stripboard.CallSheets.Services;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Persistence.Seeding;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Stripboard.Infrastructure.Services;
using Stripboard.Infrastructure.Telemetry;
using Stripboard.Mcp.Schedule.Services;
using Stripboard.Domain.Enums;
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
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddStripboardDatabase(builder.Configuration, "StripboardWebDb");

// The solver is what makes the UI real (EV-21). Before this registration the web app
// resolved ScheduleMcpService without ever providing an IScheduleSolver to satisfy it.
builder.Services.AddScoped<IScheduleSolver, CpSatScheduleSolver>();
builder.Services.AddScoped<AgentAuthorizationService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<ReplanService>();
builder.Services.AddScoped<BreakdownImportService>();
builder.Services.AddScoped<ScheduleMcpService>();
builder.Services.AddSingleton<CallSheetPdfGenerator>();
builder.Services.AddSingleton<ShootMetrics>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Stripboard.Web.Services.SentinelClient>();

// Observability (EV-20/EV-29). The exporter is configured entirely through the standard
// OTEL_* environment variables, so the Grafana Cloud credentials stay in Secret Manager
// and never appear in code — see infra/deploy-web.sh.
//
// The metrics that matter here are shoot.* : days, company moves, cost burn, risk index
// and cast utilisation. Request latency is table stakes; a production schedule burning
// budget is the thing a 1st AD would actually put on a wall.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "stripboard-web",
        // Not typeof(Program): this project references Stripboard.Mcp.Schedule, whose own
        // top-level Program makes that name ambiguous and resolves by luck rather than intent.
        serviceVersion: System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "1.0.0"))
    .WithMetrics(metrics => metrics
        .AddMeter(ShootMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

// Bring the schema up to date, then seed, before anything is served. Against the
// in-memory provider the migration step is a no-op (EV-22).
//
// An unreachable database must not stop the process from starting. Throwing here means the
// container never reaches app.Run(), so Cloud Run cold-starts crash in a loop and the public
// URL returns 503 with nothing to explain it. Stopping the Cloud SQL instance — to save money
// between demos — should degrade this app, not break it. /api/health then reports the reason
// and every page says the database is unreachable instead of showing an empty shoot.
string? databaseError = null;
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StripboardDbContext>();
    try
    {
        await DatabaseRegistration.MigrateAsync(db, app.Logger);
        await DataSeeder.SeedAsync(db);
    }
    catch (Exception ex)
    {
        databaseError = ex.Message;
        app.Logger.LogCritical(ex,
            "The database is unreachable, so this instance is serving nothing. If Cloud SQL was "
            + "stopped to save cost, restart it with: gcloud sql instances patch stripboard-db "
            + "--activation-policy=ALWAYS");
    }
}

// Solve the initial schedule only once the host has started. Doing it above would run the
// solver before the OpenTelemetry pipeline exists, so the first solve — the one that
// produces the schedule everyone sees — would never appear in solver.* metrics.
// /api/health reports degraded until this finishes, which is what the deploy script waits on.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    if (databaseError is not null)
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<StripboardDbContext>();
    var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();

    try
    {
        if (await db.ShootDays.AnyAsync(d => d.ScheduleVersionId != null))
        {
            // A schedule survived in Cloud SQL, so there is nothing to solve — but the metrics
            // live in memory and restarted empty with the process. Without this, every shoot.*
            // gauge stays silent after a redeploy even though the shoot is fully scheduled, and
            // the dashboard reads as an abandoned production.
            var existing = await schedules.GetActiveBoardAsync();
            if (existing is not null)
            {
                app.Services.GetRequiredService<ShootMetrics>().Observe(existing);
                app.Logger.LogInformation(
                    "Republished metrics for the schedule already in the database: v{Version}, {Days} day(s).",
                    existing.VersionNumber, existing.Metrics.TotalDays);
            }

            return;
        }

        await schedules.GenerateAsync(
            createdBy: AgentAuthorizationService.RoleProducer,
            startDate: new DateOnly(2026, 8, 10),
            commit: true);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Could not generate the initial schedule at startup.");
    }
}));

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
    if (databaseError is not null)
    {
        return Results.Json(
            new { status = "degraded", reason = "database unreachable", detail = databaseError },
            statusCode: 503);
    }

    ScheduleBoard? board;
    try
    {
        board = await schedules.GetActiveBoardAsync(ct);
    }
    catch (Exception ex)
    {
        // The database went away after startup. Saying so beats a 500 with a stack trace.
        return Results.Json(
            new { status = "degraded", reason = "database unreachable", detail = ex.Message },
            statusCode: 503);
    }

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

// The committed schedule, for an agent that needs to know the state before acting (EV-25).
app.MapGet("/api/schedule", async (ScheduleService schedules, CancellationToken ct) =>
{
    var board = await schedules.GetActiveBoardAsync(ct);
    if (board is null)
    {
        return Results.NotFound(new { error = "No schedule exists yet. Import a screenplay breakdown first." });
    }

    return Results.Ok(new
    {
        versionId = board.VersionId,
        board.VersionNumber,
        board.IsCommitted,
        board.CreatedBy,
        days = board.Metrics.TotalDays,
        companyMoves = board.Metrics.CompanyMoves,
        unionViolations = board.Metrics.UnionViolations,
        costUsd = board.Metrics.EstimatedCostUsd,
        scenes = board.Days.Sum(d => d.Scenes.Count),
        locations = board.Days.SelectMany(d => d.Locations).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        schedule = board.Days.Select(d => new
        {
            d.DayNumber,
            date = d.Date.ToString("yyyy-MM-dd"),
            unit = d.CallTime.Hour >= 12 ? "night" : "day",
            call = d.CallTime.ToString("HH:mm"),
            wrap = d.WrapTime.ToString("HH:mm"),
            locations = d.Locations,
            scenes = d.Scenes.Select(s => s.Number),
        }),
    });
});

// Committing is the human's decision, so this refuses any identity that is not the
// Producer — including every agent (ADR-002). An agent may call it; it will be told no.
app.MapPost("/api/schedule/commit", async (
    CommitRequest request,
    ScheduleService schedules,
    CancellationToken ct) =>
{
    try
    {
        var board = await schedules.CommitAsync(request.VersionId, request.Identity, ct);
        return Results.Ok(new
        {
            committed = true,
            board.VersionNumber,
            days = board.Metrics.TotalDays,
            costUsd = board.Metrics.EstimatedCostUsd,
        });
    }
    catch (ScheduleService.NotAuthorizedException ex)
    {
        return Results.Json(new { committed = false, error = ex.Message }, statusCode: 403);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { committed = false, error = ex.Message });
    }
});

// Replanning as an API, so an agent can obtain options without reimplementing the solver
// (EV-24). The figures returned here are differences between solved schedules; an agent's
// job is to explain them, never to produce them.
app.MapPost("/api/replan", async (
    ReplanRequest request,
    ReplanService replanner,
    StripboardDbContext db,
    CancellationToken ct) =>
{
    if (!Enum.TryParse<TriggerType>(request.TriggerType, ignoreCase: true, out var trigger))
    {
        return Results.BadRequest(new
        {
            error = $"Unknown trigger type '{request.TriggerType}'.",
            expected = Enum.GetNames<TriggerType>(),
        });
    }

    Guid? personId = null;
    if (!string.IsNullOrWhiteSpace(request.PersonName))
    {
        var person = await db.People.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name.ToLower() == request.PersonName.ToLower(), ct);
        if (person is null)
        {
            var known = await db.People.AsNoTracking().Select(p => p.Name).ToListAsync(ct);
            return Results.BadRequest(new { error = $"No cast member named '{request.PersonName}'.", known });
        }
        personId = person.Id;
    }

    try
    {
        var (disruption, options) = await replanner.ProposeAsync(new DisruptionRequest(
            trigger,
            DateOnly.Parse(request.StartDate),
            Math.Max(1, request.DurationDays),
            personId,
            request.LocationName,
            request.Description ?? $"{trigger} from {request.StartDate}"), ct: ct);

        return Results.Ok(new
        {
            disruption = new { disruption.Id, trigger = disruption.TriggerType.ToString(), disruption.Description },
            // An infeasible strategy has no metrics, so it reports null rather than zero.
            // Zeros read as measurements — "this option costs nothing" — which is the
            // opposite of what "no schedule exists" means.
            options = options.Select(o => new
            {
                versionId = o.IsFeasible ? o.VersionId : (Guid?)null,
                o.Title,
                o.Strategy,
                o.Justification,
                o.IsFeasible,
                // Non-null when this option matched an earlier one on every decision figure.
                o.SameFiguresAs,
                days = o.IsFeasible ? o.Metrics.TotalDays : (int?)null,
                companyMoves = o.IsFeasible ? o.Metrics.CompanyMoves : (int?)null,
                unionViolations = o.IsFeasible ? o.Metrics.UnionViolations : (int?)null,
                costUsd = o.IsFeasible ? o.Metrics.EstimatedCostUsd : (decimal?)null,
                delta = o.IsFeasible
                    ? new
                    {
                        o.Delta.ExtraShootDays,
                        o.Delta.ExtraCompanyMoves,
                        o.Delta.ExtraUnionViolations,
                        o.Delta.CostDeltaUsd,
                    }
                    : null,
            }),
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// A Grafana alert about schedule *quality* — "one day visits four locations" — blocks no
// scene, so /api/replan has nothing to absorb and rightly refuses. What a producer wants is
// the trade priced: how many shooting days does it cost to stop moving the unit? (EV-29)
app.MapPost("/api/schedule/consolidate", async (
    ConsolidateRequest request,
    ReplanService replanner,
    CancellationToken ct) =>
{
    try
    {
        var (current, consolidated) = await replanner.ProposeConsolidationAsync(
            request.MaxLocationsPerDay, ct: ct);

        return Results.Ok(new
        {
            options = new[] { current, consolidated }.Select(o => new
            {
                versionId = o.IsFeasible ? o.VersionId : (Guid?)null,
                o.Title,
                o.Strategy,
                o.Justification,
                o.IsFeasible,
                days = o.IsFeasible ? o.Metrics.TotalDays : (int?)null,
                companyMoves = o.IsFeasible ? o.Metrics.CompanyMoves : (int?)null,
                unionViolations = o.IsFeasible ? o.Metrics.UnionViolations : (int?)null,
                costUsd = o.IsFeasible ? o.Metrics.EstimatedCostUsd : (decimal?)null,
                delta = o.IsFeasible
                    ? new
                    {
                        o.Delta.ExtraShootDays,
                        o.Delta.ExtraCompanyMoves,
                        o.Delta.ExtraUnionViolations,
                        o.Delta.CostDeltaUsd,
                    }
                    : null,
            }),
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

/// <summary>A disruption submitted over the API by an operator or an agent.</summary>
public record ReplanRequest(
    string TriggerType,
    string StartDate,
    int DurationDays,
    string? PersonName,
    string? LocationName,
    string? Description);

/// <summary>A request to commit a schedule version, carrying who is asking.</summary>
public record CommitRequest(Guid VersionId, string Identity);

/// <summary>A request to re-solve under a hard cap on locations per shooting day.</summary>
public record ConsolidateRequest(int MaxLocationsPerDay);
