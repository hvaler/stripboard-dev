namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents an immutable, append-only audit trail event (§5 entity / ADR-003).
/// </summary>
public class AuditEvent
{
    public Guid Id { get; private set; }
    public DateTime Timestamp { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Actor { get; private set; } = string.Empty;
    public string Details { get; private set; } = string.Empty;
    public Guid? RelatedEntityId { get; private set; }

    private AuditEvent() { }

    public AuditEvent(
        Guid id,
        DateTime timestamp,
        string eventType,
        string actor,
        string details,
        Guid? relatedEntityId = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("Event type cannot be empty.", nameof(eventType));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Actor cannot be empty.", nameof(actor));

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Timestamp = timestamp;
        EventType = eventType;
        Actor = actor;
        Details = details ?? string.Empty;
        RelatedEntityId = relatedEntityId;
    }
}
