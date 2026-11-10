namespace ContaminantMigration.Core.Simulations;

public sealed class SimulationFrame
{
    internal SimulationFrame(int step, double timeSeconds, byte[] data, float minimum, float maximum)
    {
        Step = step;
        TimeSeconds = timeSeconds;
        Data = data;
        MinimumConcentration = minimum;
        MaximumConcentration = maximum;
    }

    public int Step { get; }
    public double TimeSeconds { get; }
    /// <summary>Float32 little-endian, row-major: index = y * NodesX + x, with y increasing upward.</summary>
    public ReadOnlyMemory<byte> Data { get; }
    public float MinimumConcentration { get; }
    public float MaximumConcentration { get; }
}

public sealed class SimulationResult
{
    internal SimulationResult(SimulationPlan plan, SimulationFrame[] frames, TimeSpan elapsed, string solverVersion)
    {
        Plan = plan;
        Frames = Array.AsReadOnly(frames);
        Elapsed = elapsed;
        SolverVersion = solverVersion;
    }

    public SimulationPlan Plan { get; }
    public IReadOnlyList<SimulationFrame> Frames { get; }
    public TimeSpan Elapsed { get; }
    public string SolverVersion { get; }
}
