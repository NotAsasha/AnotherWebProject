namespace ContaminantMigration.Core.Simulations;

public sealed class SimulationPlan
{
    internal SimulationPlan(SimulationParameters parameters, double dx, double dy,
        double timeStep, double stabilityRate, int totalSteps, long estimatedCellUpdates,
        long estimatedResultBytes, long estimatedWorkingBytes, int[] frameSteps)
    {
        Parameters = parameters;
        Dx = dx;
        Dy = dy;
        EffectiveTimeStepSeconds = timeStep;
        StabilityRate = stabilityRate;
        TotalSteps = totalSteps;
        EstimatedCellUpdates = estimatedCellUpdates;
        EstimatedResultBytes = estimatedResultBytes;
        EstimatedWorkingBytes = estimatedWorkingBytes;
        FrameSteps = Array.AsReadOnly(frameSteps);
    }

    public SimulationParameters Parameters { get; }
    public double Dx { get; }
    public double Dy { get; }
    public double EffectiveTimeStepSeconds { get; }
    public double StabilityRate { get; }
    public double StabilityNumber => EffectiveTimeStepSeconds * StabilityRate;
    public int TotalSteps { get; }
    public long EstimatedCellUpdates { get; }
    public long EstimatedResultBytes { get; }
    /// <summary>Two double buffers plus uncompressed Float32 frames; excludes object metadata.</summary>
    public long EstimatedWorkingBytes { get; }
    public IReadOnlyList<int> FrameSteps { get; }
}
