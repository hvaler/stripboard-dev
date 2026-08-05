using FluentAssertions;
using Stripboard.Application.Services;
using Xunit;

namespace Stripboard.Mcp.Contract.Tests;

/// <summary>
/// Least-privilege identity, at the level where it is decided (ADR-002 / ADR-004).
///
/// These tests used to pass a plain string to <c>CanExecuteCommit</c>, which is exactly the
/// hole they were meant to guard: the rule was checked against a name the caller wrote
/// itself, so an agent that wanted to commit only had to send "Producer". The role check was
/// never wrong — it was answering a question whose premise nobody had verified.
/// </summary>
public class AgentIamAuthorizationTests
{
    private readonly AgentAuthorizationService _authorization = new();

    [Theory]
    [InlineData(AgentAuthorizationService.SaSentinel)]
    [InlineData(AgentAuthorizationService.SaReplanner)]
    [InlineData(AgentAuthorizationService.SaOrchestrator)]
    [InlineData(AgentAuthorizationService.SaBreakdown)]
    public void NoAgentMayCommit_EvenWithAProvenIdentity(string serviceAccount)
    {
        // Proving who you are does not make you a Producer. This is the ADR-002 rule.
        _authorization.CanExecuteCommit(CallerIdentity.FromToken(serviceAccount)).Should().BeFalse();
    }

    [Fact]
    public void AnAgentClaimingToBeTheProducerIsStillRefused()
    {
        // The attack the old signature invited: put "Producer" in the payload and commit.
        var claim = CallerIdentity.Asserted(AgentAuthorizationService.RoleProducer);

        _authorization.CanExecuteCommit(claim).Should().BeFalse(
            "an identity nothing verified is a claim, not a credential");
    }

    [Fact]
    public void TheProducerMayCommitOnceTheIdentityIsProven()
    {
        _authorization.CanExecuteCommit(
            CallerIdentity.FromHumanSession(AgentAuthorizationService.RoleProducer))
            .Should().BeTrue();
    }

    [Fact]
    public void AServiceAccountEmailIsRecognisedAsItsRole()
    {
        // Google presents the caller as a full email. Comparing the whole string would
        // refuse the very identity the platform just proved — the agent would be locked out
        // of solving, which looks like a broken deployment rather than a policy.
        var replanner = CallerIdentity.FromToken("sa-replanner@stripboard-hack.iam.gserviceaccount.com");

        _authorization.CanExecuteSolve(replanner).Should().BeTrue();
        _authorization.CanExecuteCommit(replanner).Should().BeFalse();
    }

    [Fact]
    public void SolvingAcceptsAnAssertedIdentity_BecauseADraftBindsNobody()
    {
        // A draft schedule is a proposal a human still has to accept, so requiring proof to
        // produce one would add friction without protecting anything. The line is drawn at
        // the commit, which is where a schedule starts costing money.
        _authorization.CanExecuteSolve(
            CallerIdentity.Asserted(AgentAuthorizationService.SaScheduler)).Should().BeTrue();
    }

    [Fact]
    public void TheSentinelCanRaiseAnomaliesButNotSolveOrCommit()
    {
        // The watcher reads and reports. It has no business changing the plan.
        var sentinel = CallerIdentity.FromToken(AgentAuthorizationService.SaSentinel);

        _authorization.CanRaiseAnomaly(sentinel.Name).Should().BeTrue();
        _authorization.CanExecuteSolve(sentinel).Should().BeFalse();
        _authorization.CanExecuteCommit(sentinel).Should().BeFalse();
    }

    [Fact]
    public void AnEmptyOrAnonymousCallerIsRefusedEverything()
    {
        _authorization.CanExecuteSolve(CallerIdentity.Asserted(null)).Should().BeFalse();
        _authorization.CanExecuteCommit(CallerIdentity.Asserted("")).Should().BeFalse();
        _authorization.CanRaiseAnomaly("  ").Should().BeFalse();
    }
}
