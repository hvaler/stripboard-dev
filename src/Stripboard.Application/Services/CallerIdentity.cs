namespace Stripboard.Application.Services;

/// <summary>
/// Who is calling, and whether anything other than their own word says so.
///
/// Until now every authorisation check took an identity as a plain string that the caller
/// supplied — `commit_schedule(versionId, identity)`. The rule "only a Producer may commit"
/// was therefore enforced against a claim the agent wrote itself, and an agent that wanted
/// to commit only had to pass "Producer". The refusal in the demo was real; the guarantee
/// behind it was not.
///
/// <see cref="IsAuthenticated"/> is the distinction that matters. It is true only when the
/// platform proved the caller: a Google-signed identity token that Cloud Run validated
/// before the request reached us, or a human session. A name that arrived in a JSON payload
/// is <em>asserted</em>, and an asserted identity can propose all it likes but can never
/// commit.
/// </summary>
/// <param name="Name">The role or service account, e.g. "Producer" or "sa-replanner".</param>
/// <param name="IsAuthenticated">True when the platform proved it, false when self-declared.</param>
/// <param name="Source">How we know — "oidc", "human-session", or "asserted".</param>
public sealed record CallerIdentity(string Name, bool IsAuthenticated, string Source)
{
    public const string SourceAsserted = "asserted";
    public const string SourceOidc = "oidc";
    public const string SourceHumanSession = "human-session";

    /// <summary>A name the caller supplied and nothing verified.</summary>
    public static CallerIdentity Asserted(string? name) =>
        new(string.IsNullOrWhiteSpace(name) ? "anonymous" : name.Trim(), false, SourceAsserted);

    /// <summary>A service account proven by a platform-validated identity token.</summary>
    public static CallerIdentity FromToken(string serviceAccount) =>
        new(serviceAccount, true, SourceOidc);

    /// <summary>
    /// A human at a browser session. The demo lets the operator choose which role they are
    /// acting as, and that choice is recorded on the audit trail with every commit — the
    /// governance story is about agents not being able to decide, not about the human's
    /// login, which a production deployment would put behind IAP.
    /// </summary>
    public static CallerIdentity FromHumanSession(string role) =>
        new(role, true, SourceHumanSession);

    public override string ToString() => IsAuthenticated ? Name : $"{Name} (unverified)";
}
