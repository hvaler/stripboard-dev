using Stripboard.Application.Common.Models;

namespace Stripboard.Application.Common.Interfaces;

/// <summary>
/// Core contract for schedule optimization engine (§5 / ADR-001 / ADR-002).
/// Formulates and solves film shooting schedule using deterministic CP-SAT solver.
/// </summary>
public interface IScheduleSolver
{
    Task<SolverOutput> SolveAsync(SolverInput input, CancellationToken cancellationToken = default);
}
