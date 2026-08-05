namespace Stripboard.Application.Services;

public class AgentAuthorizationService
{
    public const string RoleProducer = "Producer";
    public const string SaBreakdown = "sa-breakdown";
    public const string SaScheduler = "sa-scheduler";
    public const string SaSentinel = "sa-sentinel";
    public const string SaReplanner = "sa-replanner";
    public const string SaCallsheets = "sa-callsheets";
    public const string SaOrchestrator = "sa-orchestrator";

    /// <summary>
    /// Checks whether the identity has permission to execute CP-SAT solver (ADR-004).
    ///
    /// Solving is safe: it produces a draft nobody has to accept, so an asserted identity is
    /// enough. Committing is not, which is why <see cref="CanExecuteCommit"/> asks for more.
    /// </summary>
    public bool CanExecuteSolve(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;

        return Matches(identity, RoleProducer)
            || Matches(identity, SaScheduler)
            || Matches(identity, SaReplanner)
            || Matches(identity, SaOrchestrator);
    }

    public bool CanExecuteSolve(CallerIdentity identity) => CanExecuteSolve(identity.Name);

    /// <summary>
    /// Checks whether the caller may commit a schedule (ADR-002, human-in-the-loop).
    ///
    /// Two conditions, and the second is the one that was missing. The caller must be the
    /// Producer role <em>and</em> the platform must have proved they are — a name that
    /// arrived in a request body is a claim, not a credential, and an agent asked not to
    /// commit will happily send `identity: "Producer"` if that is all it takes.
    /// </summary>
    public bool CanExecuteCommit(CallerIdentity identity)
    {
        if (identity is null || !identity.IsAuthenticated) return false;

        return Matches(identity.Name, RoleProducer);
    }

    /// <summary>
    /// Role-only form, for callers that have already established the identity is genuine.
    /// Prefer the <see cref="CallerIdentity"/> overload: this one cannot tell a proven
    /// Producer from a service account that typed the word.
    /// </summary>
    public bool HasCommitRole(string identity) =>
        !string.IsNullOrWhiteSpace(identity) && Matches(identity, RoleProducer);

    /// <summary>
    /// Checks whether the identity has permission to raise an anomaly event (ADR-004).
    /// </summary>
    public bool CanRaiseAnomaly(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;

        return Matches(identity, RoleProducer) || Matches(identity, SaSentinel);
    }

    /// <summary>
    /// A service account arrives from Google as a full email — sa-replanner@project.iam
    /// .gserviceaccount.com — so comparing the whole string against "sa-replanner" would
    /// refuse the very identity the platform just proved.
    /// </summary>
    private static bool Matches(string identity, string role)
    {
        var name = identity.Trim();
        var at = name.IndexOf('@');
        if (at > 0)
        {
            name = name[..at];
        }

        return name.Equals(role, StringComparison.OrdinalIgnoreCase);
    }
}
