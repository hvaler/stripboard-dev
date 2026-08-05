using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Stripboard.Mcp.People.Services;

namespace Stripboard.Mcp.People.Tools;

/// <summary>
/// Cast and crew, over the Model Context Protocol (EV-23).
///
/// The Day Out of Days is the document a 1st AD lives by: for every cast member, which days
/// they work, which days they are held, and which day they are dropped. It is what turns an
/// actor's contract into money, so it is the first thing an agent should be able to read.
/// </summary>
[McpServerToolType]
public sealed class PeopleTools
{
    private readonly PeopleMcpService _people;

    public PeopleTools(PeopleMcpService people)
        => _people = people ?? throw new ArgumentNullException(nameof(people));

    [McpServerTool(Name = "get_person")]
    [Description("Look up one cast or crew member: their name, role and day rate.")]
    public async Task<object> GetPersonAsync(
        [Description("The person's id.")] Guid personId,
        CancellationToken ct = default)
    {
        var person = await _people.GetPersonAsync(personId, ct)
            ?? throw new McpException($"No person with id {personId}.");

        return new
        {
            id = person.Id,
            person.Name,
            role = person.Role.ToString(),
            dayRateUsd = person.DailyRate,
            isCast = person.IsCast,
        };
    }

    [McpServerTool(Name = "get_dood")]
    [Description("Day Out of Days for one cast member across a date range: W work, H hold, "
               + "P pickup, D drop, OFF not called. Days held but not worked are days the "
               + "production pays for and does not use.")]
    public async Task<object> GetDoodAsync(
        [Description("The cast member's id.")] Guid personId,
        [Description("First date of the range, ISO format (YYYY-MM-DD).")] string startDate,
        [Description("Last date of the range, ISO format (YYYY-MM-DD).")] string endDate,
        CancellationToken ct = default)
    {
        var start = IsoDate.Parse(startDate);
        var end = IsoDate.Parse(endDate);
        if (end < start)
        {
            throw new McpException($"The range ends ({endDate}) before it starts ({startDate}).");
        }

        var dood = await _people.GetDoodAsync(personId, start, end, ct);
        return new
        {
            dood.PersonId,
            dood.PersonName,
            days = dood.Days.Select(d => new { date = d.Date.ToString("yyyy-MM-dd"), d.Status }),
            worked = dood.Days.Count(d => d.Status == "W"),
            heldNotWorked = dood.Days.Count(d => d.Status == "H"),
        };
    }

    [McpServerTool(Name = "update_availability")]
    [Description("Record the dates a cast member is unavailable. The solver treats these as hard "
               + "constraints, so this changes what schedules are possible.")]
    public async Task<object> UpdateAvailabilityAsync(
        [Description("The cast member's id.")] Guid personId,
        [Description("Dates they cannot work, ISO format (YYYY-MM-DD).")] string[] unavailableDates,
        CancellationToken ct = default)
    {
        var parsed = (unavailableDates ?? []).Select(IsoDate.Parse).ToList();

        // The service answers false when the person does not exist. Returning that as a
        // successful-looking result would let a caller believe an absence was recorded.
        var updated = await _people.UpdateAvailabilityAsync(personId, parsed, ct);
        if (!updated)
        {
            throw new McpException($"No person with id {personId}; nothing was recorded.");
        }

        return new { personId, unavailableDays = parsed.Count };
    }
}
