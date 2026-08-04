namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents a single day of shooting on the schedule (§5 entity).
/// </summary>
public class ShootDay
{
    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public int DayNumber { get; private set; }
    public string LocationName { get; private set; } = string.Empty;
    public TimeOnly CallTime { get; private set; }
    public TimeOnly EstimatedWrapTime { get; private set; }
    public List<Guid> StripIds { get; private set; } = new();

    /// <summary>
    /// The schedule version this day belongs to. Null for days not tied to a version,
    /// such as the transient ShootDay the solver builds to validate turnaround.
    /// </summary>
    public Guid? ScheduleVersionId { get; private set; }

    private ShootDay() { }

    public ShootDay(
        Guid id,
        DateOnly date,
        int dayNumber,
        string locationName,
        TimeOnly callTime,
        TimeOnly estimatedWrapTime,
        IEnumerable<Guid>? stripIds = null,
        Guid? scheduleVersionId = null)
    {
        if (dayNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(dayNumber), "Day number must be positive.");

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Date = date;
        DayNumber = dayNumber;
        LocationName = locationName ?? string.Empty;
        CallTime = callTime;
        EstimatedWrapTime = estimatedWrapTime;
        StripIds = stripIds?.ToList() ?? new List<Guid>();
        ScheduleVersionId = scheduleVersionId;
    }

    /// <summary>
    /// Gets the full DateTime for call time.
    /// </summary>
    public DateTime GetCallDateTime()
    {
        return Date.ToDateTime(CallTime);
    }

    /// <summary>
    /// Gets the full DateTime for wrap time, handling overnight wraps correctly.
    /// </summary>
    public DateTime GetWrapDateTime()
    {
        var wrapDateTime = Date.ToDateTime(EstimatedWrapTime);
        if (EstimatedWrapTime < CallTime)
        {
            // Overnight shoot: wrap occurred on the following calendar day
            wrapDateTime = Date.AddDays(1).ToDateTime(EstimatedWrapTime);
        }
        return wrapDateTime;
    }
}
