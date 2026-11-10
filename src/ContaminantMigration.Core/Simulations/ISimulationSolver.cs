namespace ContaminantMigration.Core.Simulations;

public interface ISimulationSolver
{
    SimulationResult Solve(SimulationParameters parameters,
        Action<SimulationProgress>? progress = null, CancellationToken cancellationToken = default);
}
