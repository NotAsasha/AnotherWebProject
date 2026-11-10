namespace ContaminantMigration.Core.Simulations;

public sealed class SimulationTimeoutException(TimeSpan limit)
    : TimeoutException($"Simulation exceeded its execution limit of {limit}.")
{
    public TimeSpan Limit { get; } = limit;
}

public sealed class SimulationNumericalException(int step, int nodeIndex, double value)
    : ArithmeticException($"Invalid concentration {value} at step {step}, node {nodeIndex}.")
{
    public int Step { get; } = step;
    public int NodeIndex { get; } = nodeIndex;
    public double Value { get; } = value;
}
