namespace Stripboard.Domain.Entities;

/// <summary>
/// Represents an immutable, append-only schedule version (§5 entity).
/// </summary>
public class ScheduleVersion
{
    public Guid Id { get; private set; }
    public int VersionNumber { get; private set; }
    public Guid? ParentId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;
    public Guid? DisruptionId { get; private set; }
    public bool IsCommitted { get; private set; }

    private ScheduleVersion() { }

    public ScheduleVersion(
        Guid id,
        int versionNumber,
        Guid? parentId = null,
        DateTime? createdAt = null,
        string createdBy = "system",
        Guid? disruptionId = null,
        bool isCommitted = false)
    {
        if (versionNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(versionNumber), "Version number must be positive.");

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        VersionNumber = versionNumber;
        ParentId = parentId;
        CreatedAt = createdAt ?? DateTime.UtcNow;
        CreatedBy = createdBy ?? "system";
        DisruptionId = disruptionId;
        IsCommitted = isCommitted;
    }

    public void Commit()
    {
        IsCommitted = true;
    }
}
