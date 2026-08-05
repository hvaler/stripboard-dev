using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stripboard.Infrastructure.Persistence;

/// <summary>
/// One place that decides how the database is reached (EV-22).
///
/// Five services used to carry their own copy of this decision, and all five said
/// "in-memory" while the README said Cloud SQL. Now the connection string decides: present
/// means PostgreSQL, absent means an in-memory database that says so loudly at startup.
/// </summary>
public static class DatabaseRegistration
{
    public const string ConnectionName = "Stripboard";

    /// <summary>
    /// Resolves the connection string from configuration or the environment. Cloud Run
    /// injects it from Secret Manager; a developer can export it for a local Postgres.
    /// </summary>
    public static string? ResolveConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionName)
        ?? Environment.GetEnvironmentVariable("STRIPBOARD_DB_CONNECTION");

    public static IServiceCollection AddStripboardDatabase(
        this IServiceCollection services,
        IConfiguration configuration,
        string inMemoryDatabaseName)
    {
        var connectionString = ResolveConnectionString(configuration);

        services.AddDbContext<StripboardDbContext>((provider, options) =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                // Deliberately noisy. An in-memory database looks identical to a working
                // one until the service restarts and the audit trail has vanished.
                provider.GetService<ILoggerFactory>()?
                    .CreateLogger(typeof(DatabaseRegistration))
                    .LogWarning(
                        "No '{Connection}' connection string: using an IN-MEMORY database. "
                        + "Every schedule, disruption and audit event will be lost on restart.",
                        ConnectionName);

                options.UseInMemoryDatabase(inMemoryDatabaseName);
                return;
            }

            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null))
                   .UseSnakeCaseNamingConvention();
        });

        return services;
    }

    /// <summary>
    /// Applies pending migrations when running against a real database. Safe to call at
    /// startup: it is a no-op for the in-memory provider, which has no migrations.
    /// </summary>
    public static async Task MigrateAsync(StripboardDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            logger?.LogInformation("Database schema is up to date.");
            return;
        }

        logger?.LogInformation("Applying {Count} pending migration(s): {Migrations}",
            pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
    }
}
