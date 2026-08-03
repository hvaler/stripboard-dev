using Microsoft.EntityFrameworkCore;
using Stripboard.Domain.Entities;
using Stripboard.Infrastructure.Persistence;

namespace Stripboard.Mcp.People.Services;

public record DoodDayStatus(DateOnly Date, string Status); // "P" (Pickup), "W" (Work), "H" (Hold), "D" (Drop), "OFF"
public record DoodResult(Guid PersonId, string PersonName, List<DoodDayStatus> Days);

public class PeopleMcpService
{
    private readonly StripboardDbContext _dbContext;

    public PeopleMcpService(StripboardDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <summary>
    /// MCP Tool: get_person(person_id)
    /// </summary>
    public async Task<Person?> GetPersonAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.People.FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
    }

    /// <summary>
    /// MCP Tool: get_dood(person_id, start_date, end_date)
    /// Computes Day Out of Days (DOOD) matrix for actor.
    /// </summary>
    public async Task<DoodResult> GetDoodAsync(Guid personId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        var person = await _dbContext.People.FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
        var name = person?.Name ?? "Unknown";

        var shootDays = await _dbContext.ShootDays
            .Where(sd => sd.Date >= startDate && sd.Date <= endDate)
            .OrderBy(sd => sd.Date)
            .ToListAsync(cancellationToken);

        var doodDays = new List<DoodDayStatus>();
        int totalDays = endDate.DayNumber - startDate.DayNumber + 1;

        for (int i = 0; i < totalDays; i++)
        {
            var date = startDate.AddDays(i);
            bool isWorkDay = shootDays.Any(sd => sd.Date == date);

            string status = isWorkDay ? "W" : "OFF";
            doodDays.Add(new DoodDayStatus(date, status));
        }

        // Apply Pickup (P), Drop (D), Hold (H) logic
        var workIndices = doodDays.Select((d, idx) => d.Status == "W" ? idx : -1).Where(idx => idx >= 0).ToList();
        if (workIndices.Count > 0)
        {
            int firstWork = workIndices.First();
            int lastWork = workIndices.Last();

            doodDays[firstWork] = new DoodDayStatus(doodDays[firstWork].Date, "P");
            if (lastWork != firstWork)
            {
                doodDays[lastWork] = new DoodDayStatus(doodDays[lastWork].Date, "D");
            }

            for (int i = firstWork + 1; i < lastWork; i++)
            {
                if (doodDays[i].Status == "OFF")
                {
                    doodDays[i] = new DoodDayStatus(doodDays[i].Date, "H");
                }
            }
        }

        return new DoodResult(personId, name, doodDays);
    }

    /// <summary>
    /// MCP Tool: update_availability(person_id, unavailable_dates)
    /// </summary>
    public async Task<bool> UpdateAvailabilityAsync(Guid personId, List<DateOnly> unavailableDates, CancellationToken cancellationToken = default)
    {
        var person = await _dbContext.People.FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
        if (person == null) return false;

        var auditEvent = new AuditEvent(
            Guid.NewGuid(),
            DateTime.UtcNow,
            eventType: "AvailabilityUpdated",
            actor: "mcp-people",
            details: $"Updated availability for {person.Name}. {unavailableDates.Count} unavailable dates marked.",
            relatedEntityId: personId
        );

        _dbContext.AuditEvents.Add(auditEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
