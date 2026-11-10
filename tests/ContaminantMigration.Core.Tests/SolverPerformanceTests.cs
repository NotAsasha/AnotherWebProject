using ContaminantMigration.Core.Simulations;
using Xunit.Abstractions;

namespace ContaminantMigration.Core.Tests;

[CollectionDefinition("Solver benchmarks", DisableParallelization = true)]
public sealed class SolverBenchmarkCollection;

[Collection("Solver benchmarks")]
public sealed class SolverPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasureRuntimeAndAllocationsWithinTheConfiguredWorkBudget(bool large)
    {
        var parameters = SimulationCases.Demo;
        if (large)
        {
            const int nodes = 512;
            var dx = 100.0 / (nodes - 1);
            var rate = 0.6 / dx + 0.4 / dx / dx;
            parameters = parameters with
            {
                Domain = new(100, 100, nodes, nodes), Time = new(750 * 0.9 / rate), RequestedFrames = 32
            };
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new FdmSimulationSolver().Solve(parameters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Grid={parameters.Domain.NodesX}x{parameters.Domain.NodesY}; steps={result.Plan.TotalSteps}; "
            + $"cell updates={result.Plan.EstimatedCellUpdates}; elapsed={result.Elapsed.TotalMilliseconds:F1}ms; "
            + $"allocated={allocated:N0}B; estimated buffers+frames={result.Plan.EstimatedWorkingBytes:N0}B");
        Assert.True(allocated <= result.Plan.EstimatedWorkingBytes + 128 * 1024,
            "Allocation substantially exceeds the two buffers and retained frames.");
        Assert.Equal(result.Plan.FrameSteps.Count, result.Frames.Count);
    }
}
