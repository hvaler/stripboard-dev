using Microsoft.EntityFrameworkCore;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence;

/// <summary>
/// Entity Framework Core DbContext for Stripboard persistence (§5 / ADR-006).
/// Uses PostgreSQL (Npgsql) with snake_case naming conventions and append-only audit tracking.
/// </summary>
public class StripboardDbContext : DbContext
{
    public DbSet<Scene> Scenes => Set<Scene>();
    public DbSet<Element> Elements => Set<Element>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Strip> Strips => Set<Strip>();
    public DbSet<ShootDay> ShootDays => Set<ShootDay>();
    public DbSet<ScheduleVersion> ScheduleVersions => Set<ScheduleVersion>();
    public DbSet<Disruption> Disruptions => Set<Disruption>();
    public DbSet<Anomaly> Anomalies => Set<Anomaly>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public StripboardDbContext(DbContextOptions<StripboardDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StripboardDbContext).Assembly);
    }
}
