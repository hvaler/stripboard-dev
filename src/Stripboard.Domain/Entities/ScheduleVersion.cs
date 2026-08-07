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

    /// <summary>
    /// Who approved this version, and when — empty until somebody does.
    ///
    /// This is deliberately *not* <see cref="CreatedBy"/>. A version is proposed by whoever ran
    /// the solver, which is usually an agent; it is committed by a human Producer, and only a
    /// human Producer. Collapsing the two into one field made the board read
    /// "Committed · created by sa-replanner", which says the opposite of the rule the service
    /// actually enforces — and a reader believes the screen over the README.
    /// </summary>
    public string? ApprovedBy { get; private set; }

    public DateTime? ApprovedAt { get; private set; }

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

    /// <summary>
    /// Commits this version in the name of the caller who approved it.
    ///
    /// The approver is a required argument rather than an optional one, so a committed version
    /// without a recorded approver cannot be produced by this code at all. The domain will not
    /// stop a caller who has no business committing — that is the service's job, against an
    /// identity the platform proved (ADR-020) — but it will not let the fact go unrecorded.
    /// </summary>
    public void Commit(string approvedBy, DateTime? approvedAt = null)
    {
        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new ArgumentException(
                "A commit has to name who approved it. Recording an empty approver is how "
                + "'created by' ends up standing in for it.", nameof(approvedBy));
        }

        IsCommitted = true;
        ApprovedBy = approvedBy;
        ApprovedAt = approvedAt ?? DateTime.UtcNow;
    }
}
