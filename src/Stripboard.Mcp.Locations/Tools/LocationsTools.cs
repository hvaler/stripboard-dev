using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Stripboard.Mcp.Locations.Services;

namespace Stripboard.Mcp.Locations.Tools;

/// <summary>
/// Locations and their permit windows, over the Model Context Protocol (EV-23).
///
/// A location is the place the trucks park, not the room the scene is in. That distinction
/// is what makes a company move real, and it is why these tools key on location rather than
/// on the set named in a scene heading (ADR-013).
/// </summary>
[McpServerToolType]
public sealed class LocationsTools
{
    private readonly LocationsMcpService _locations;

    public LocationsTools(LocationsMcpService locations)
        => _locations = locations ?? throw new ArgumentNullException(nameof(locations));

    [McpServerTool(Name = "get_location")]
    [Description("Look up a shooting location: how many scenes are scheduled there and its status.")]
    public async Task<object> GetLocationAsync(
        [Description("Location name as it appears in the scene headings, e.g. '221B BAKER STREET'.")]
        string locationName,
        CancellationToken ct = default)
    {
        var info = await _locations.GetLocationAsync(locationName, ct);
        if (info is null)
        {
            // Naming the alternatives is what turns a dead end into something the caller can
            // act on — the same reason /api/replan lists the cast when a name does not match.
            var known = await _locations.GetKnownLocationsAsync(ct);
            throw new McpException(
                $"No location named '{locationName}' appears in this production. "
                + (known.Count > 0
                    ? $"Known locations: {string.Join(", ", known)}."
                    : "No screenplay breakdown has been imported yet."));
        }

        return new { info.LocationName, info.ScenesScheduledCount, info.Status };
    }

    [McpServerTool(Name = "get_permits")]
    [Description("The permit windows for a location. Shooting outside a window is not a delay, "
               + "it is a shutdown, so these are hard constraints on the schedule.")]
    public async Task<object> GetPermitsAsync(
        [Description("Location name, e.g. 'TOWER BRIDGE WHARF'.")] string locationName,
        CancellationToken ct = default)
    {
        var permits = await _locations.GetPermitsAsync(locationName, ct);
        return new
        {
            locationName,
            // An empty list means no permit is on file — which is not the same as "shoot
            // whenever you like", and the caller has to be able to tell the two apart.
            permitted = permits.Count > 0,
            windows = permits.Select(p => new
            {
                p.LocationName,
                from = p.StartDate.ToString("yyyy-MM-dd"),
                to = p.EndDate.ToString("yyyy-MM-dd"),
            }),
        };
    }

    [McpServerTool(Name = "check_access")]
    [Description("Whether a location can be shot on a given date, and why not if it cannot.")]
    public async Task<object> CheckAccessAsync(
        [Description("Location name.")] string locationName,
        [Description("The date to check, ISO format (YYYY-MM-DD).")] string date,
        CancellationToken ct = default)
    {
        var access = await _locations.CheckAccessAsync(locationName, IsoDate.Parse(date), ct);
        return new
        {
            access.LocationName,
            date = access.Date.ToString("yyyy-MM-dd"),
            access.HasAccess,
            access.Details,
        };
    }
}
