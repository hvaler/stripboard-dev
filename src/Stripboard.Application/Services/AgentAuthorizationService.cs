namespace Stripboard.Application.Services;

public class AgentAuthorizationService
{
    public const string RoleProducer = "Producer";
    public const string SaBreakdown = "sa-breakdown";
    public const string SaScheduler = "sa-scheduler";
    public const string SaSentinel = "sa-sentinel";
    public const string SaReplanner = "sa-replanner";
    public const string SaCallsheets = "sa-callsheets";

    /// <summary>
    /// Checks whether the identity has permission to execute CP-SAT solver (ADR-004).
    /// </summary>
    public bool CanExecuteSolve(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        
        return identity.Equals(RoleProducer, StringComparison.OrdinalIgnoreCase) ||
               identity.Equals(SaScheduler, StringComparison.OrdinalIgnoreCase) ||
               identity.Equals(SaReplanner, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether the identity has permission to commit a schedule (ADR-002 Human-in-the-Loop).
    /// ONLY Human Producer has permission to commit. Agents CANNOT commit schedules.
    /// </summary>
    public bool CanExecuteCommit(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        
        return identity.Equals(RoleProducer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether the identity has permission to raise an anomaly event (ADR-004).
    /// </summary>
    public bool CanRaiseAnomaly(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;

        return identity.Equals(RoleProducer, StringComparison.OrdinalIgnoreCase) ||
               identity.Equals(SaSentinel, StringComparison.OrdinalIgnoreCase);
    }
}
