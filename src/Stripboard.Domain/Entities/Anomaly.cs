using Stripboard.Domain.Enums;

namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents a detected anomaly or rule violation in the shooting schedule (§5 entity).
/// </summary>
public class Anomaly
{
    public Guid Id { get; private set; }
    public AnomalySeverity Severity { get; private set; }
    public AnomalyType Type { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public List<Guid> SceneIds { get; private set; } = new();
    public DateTime Timestamp { get; private set; }

    private Anomaly() { }

    public Anomaly(
        Guid id,
        AnomalySeverity severity,
        AnomalyType type,
        string message,
        IEnumerable<Guid>? sceneIds = null,
        DateTime? timestamp = null)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Severity = severity;
        Type = type;
        Message = message ?? string.Empty;
        SceneIds = sceneIds?.ToList() ?? new List<Guid>();
        Timestamp = timestamp ?? DateTime.UtcNow;
    }
}
