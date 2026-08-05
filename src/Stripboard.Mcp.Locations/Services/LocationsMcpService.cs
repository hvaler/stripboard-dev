using Microsoft.EntityFrameworkCore;
using Stripboard.Application.Common.Models;
using Stripboard.Infrastructure.Persistence;

namespace Stripboard.Mcp.Locations.Services;

public record LocationInfoResult(string LocationName, int ScenesScheduledCount, string Status);
public record CheckAccessResult(string LocationName, DateOnly Date, bool HasAccess, string Details);

public class LocationsMcpService
{
    private readonly StripboardDbContext _dbContext;

    public LocationsMcpService(StripboardDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <summary>
    /// MCP Tool: get_location(location_name)
    /// </summary>
    public async Task<LocationInfoResult?> GetLocationAsync(string locationName, CancellationToken cancellationToken = default)
    {
        var count = await _dbContext.Scenes
            .CountAsync(s => s.SetLocation.ToLower() == locationName.ToLower(), cancellationToken);

        // A location exists because scenes happen there. Nothing else defines one, so zero
        // scenes means the name is not part of this production — and this used to answer
        // "Available" for any string at all, inventing a location on demand.
        return count == 0 ? null : new LocationInfoResult(locationName, count, "Active");
    }

    /// <summary>Every location the production actually visits, so a caller can recover from a typo.</summary>
    public async Task<List<string>> GetKnownLocationsAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.Scenes
            .Select(s => s.SetLocation)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// MCP Tool: get_permits(location_name)
    /// </summary>
    public Task<List<LocationPermitWindow>> GetPermitsAsync(string locationName, CancellationToken cancellationToken = default)
    {
        // Demo permit windows for locations
        var windows = new List<LocationPermitWindow>
        {
            new("221B BAKER STREET", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 30)),
            new("TOWER BRIDGE WHARF", new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 20))
        };

        var match = windows.Where(w => w.LocationName.Equals(locationName, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(match);
    }

    /// <summary>
    /// MCP Tool: check_access(location_name, date)
    /// </summary>
    public async Task<CheckAccessResult> CheckAccessAsync(string locationName, DateOnly date, CancellationToken cancellationToken = default)
    {
        var permits = await GetPermitsAsync(locationName, cancellationToken);
        bool hasAccess = permits.Any(p => date >= p.StartDate && date <= p.EndDate);

        string details = hasAccess
            ? $"Access granted for {locationName} on {date}."
            : $"Access denied: No active permit window for {locationName} on {date}.";

        return new CheckAccessResult(locationName, date, hasAccess, details);
    }
}
