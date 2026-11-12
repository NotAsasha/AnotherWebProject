namespace ContaminantMigration.Core.Simulations;

public sealed record SimulationProgress(int CompletedSteps, int TotalSteps, double SimulationTimeSeconds)
{    public double Percent => Math.Min(99, 100.0 * CompletedSteps / TotalSteps);
}
