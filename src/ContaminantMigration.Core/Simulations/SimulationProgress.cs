namespace ContaminantMigration.Core.Simulations;

public sealed record SimulationProgress(int CompletedSteps, int TotalSteps, double SimulationTimeSeconds)
{
    // The application sets 100% only after persisting the successful result.
    public double Percent => Math.Min(99, 100.0 * CompletedSteps / TotalSteps);
}
