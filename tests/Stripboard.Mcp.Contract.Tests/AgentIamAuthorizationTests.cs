using FluentAssertions;
using Stripboard.Application.Services;
using Xunit;

namespace Stripboard.Mcp.Contract.Tests;

public class AgentIamAuthorizationTests
{
    private readonly AgentAuthorizationService _authService = new();

    [Fact]
    public void SentinelAgent_CannotExecuteSolve_NegativeTest()
    {
        // Act
        bool canSolve = _authService.CanExecuteSolve("sa-sentinel");

        // Assert
        canSolve.Should().BeFalse("sa-sentinel is a read-only monitoring identity and cannot invoke solver");
    }

    [Fact]
    public void SentinelAgent_CannotExecuteCommit_NegativeTest()
    {
        // Act
        bool canCommit = _authService.CanExecuteCommit("sa-sentinel");

        // Assert
        canCommit.Should().BeFalse("sa-sentinel cannot commit schedules");
    }

    [Fact]
    public void ReplannerAgent_CannotExecuteCommit_NegativeTest()
    {
        // Act
        bool canCommit = _authService.CanExecuteCommit("sa-replanner");

        // Assert
        canCommit.Should().BeFalse("ADR-002 enforces Human-in-the-Loop; sa-replanner can only propose draft options");
    }

    [Fact]
    public void Producer_CanExecuteCommit_PositiveTest()
    {
        // Act
        bool canCommit = _authService.CanExecuteCommit("Producer");

        // Assert
        canCommit.Should().BeTrue("Human Producer is authorized to commit schedule versions");
    }

    [Fact]
    public void SentinelAgent_CanRaiseAnomaly_PositiveTest()
    {
        // Act
        bool canRaise = _authService.CanRaiseAnomaly("sa-sentinel");

        // Assert
        canRaise.Should().BeTrue("sa-sentinel is authorized to emit anomaly events and Grafana annotations");
    }
}
