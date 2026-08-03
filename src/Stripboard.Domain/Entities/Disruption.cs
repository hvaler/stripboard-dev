using Stripboard.Domain.Enums;

namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents a disruption event affecting the shooting schedule (§5 entity).
/// </summary>
public class Disruption
{
    public Guid Id { get; private set; }
    public DateTime Timestamp { get; private set; }
    public TriggerType TriggerType { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public Guid? PersonId { get; private set; }
    public Guid? LocationId { get; private set; }
    public int ExpectedDurationDays { get; private set; }

    private Disruption() { }

    public Disruption(
        Guid id,
        DateTime timestamp,
        TriggerType triggerType,
        string description,
        Guid? personId = null,
        Guid? locationId = null,
        int expectedDurationDays = 1)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Timestamp = timestamp;
        TriggerType = triggerType;
        Description = description ?? string.Empty;
        PersonId = personId;
        LocationId = locationId;
        ExpectedDurationDays = expectedDurationDays > 0 ? expectedDurationDays : 1;
    }
}
