using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stripboard.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef migrations` build a DbContext without starting the web app.
///
/// The connection string here is only used to pick the provider and generate SQL — no
/// database is contacted when scaffolding a migration — so a placeholder is enough and
/// nothing secret belongs in this file. Set STRIPBOARD_DB_CONNECTION to point the tooling
/// at a real database for `dotnet ef database update`.
/// </summary>
public sealed class StripboardDbContextFactory : IDesignTimeDbContextFactory<StripboardDbContext>
{
    private const string ScaffoldingPlaceholder =
        "Host=localhost;Port=5432;Database=stripboard;Username=postgres;Password=postgres";

    public StripboardDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("STRIPBOARD_DB_CONNECTION")
                               ?? ScaffoldingPlaceholder;

        var options = new DbContextOptionsBuilder<StripboardDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new StripboardDbContext(options);
    }
}
