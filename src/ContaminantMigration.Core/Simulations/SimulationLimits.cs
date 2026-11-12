namespace ContaminantMigration.Core.Simulations;

public sealed record SimulationLimits
{
    public static SimulationLimits Default { get; } = new();

    public double MaxDomainLength { get; init; } = 10_000;
    public int MaxNodesPerAxis { get; init; } = 512;
    public double MaxAbsoluteVelocity { get; init; } = 100;
    public double MaxDiffusion { get; init; } = 100;
    public double MaxDurationSeconds { get; init; } = 86_400;
    public double MaxPeak { get; init; } = 1_000_000;
    public int MaxTimeSteps { get; init; } = 10_000;
    public long MaxCellUpdates { get; init; } = 200_000_000_000;
    public int MaxFrames { get; init; } = 32;
    public long MaxResultBytes { get; init; } = 32 * 1024 * 1024;
    public long MaxWorkingBytes { get; init; } = 64 * 1024 * 1024;
    public TimeSpan MaxExecutionTime { get; init; } = TimeSpan.FromSeconds(60);

    internal void Validate()
    {
        if (!IsPositiveFinite(MaxDomainLength) || MaxNodesPerAxis < 3
            || !IsPositiveFinite(MaxAbsoluteVelocity) || !IsPositiveFinite(MaxDiffusion)
            || !IsPositiveFinite(MaxDurationSeconds) || !IsPositiveFinite(MaxPeak) || MaxPeak > float.MaxValue
            || MaxTimeSteps < 1 || MaxTimeSteps == int.MaxValue || MaxCellUpdates < 1
            || MaxFrames < 2 || MaxResultBytes < 1 || MaxWorkingBytes < 1
            || MaxExecutionTime <= TimeSpan.Zero)
            throw new ArgumentException("Simulation limits must be finite and positive, with valid grid and frame bounds.");
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
