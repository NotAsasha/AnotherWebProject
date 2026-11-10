using ContaminantMigration.Core.Simulations;

namespace ContaminantMigration.Core.Tests;

public sealed class FdmSimulationSolverTests
{
    private readonly FdmSimulationSolver solver = new();

    [Fact]
    public void ZeroFieldRemainsZero()
    {
        var demo = SimulationCases.Demo;
        var result = solver.Solve(demo with { InitialCondition = demo.InitialCondition with { Peak = 0 } });
        Assert.All(result.Frames, frame => Assert.Equal(-1, frame.Data.Span.IndexOfAnyExcept((byte)0)));
    }

    [Fact]
    public void NoTransportOrDiffusionPreservesTheInitialField()
    {
        var result = solver.Solve(SimulationCases.Demo with { Transport = new(0, 0, 0) });
        Assert.True(result.Frames[0].Data.Span.SequenceEqual(result.Frames[^1].Data.Span));
        Assert.Equal(60, result.Frames[^1].TimeSeconds);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    public void CourantOneTranslatesExactlyOnARectangularGrid(int velocityX, int velocityY)
    {
        var parameters = new SimulationParameters(new(120, 80, 121, 81), new(velocityX, velocityY, 0),
            new(3, 1), new(1, 60, 40, 5), RequestedFrames: 2);
        var result = solver.Solve(parameters);
        var first = result.Frames[0];
        var last = result.Frames[^1];
        for (var j = 1; j < 80; j++)
        for (var i = 1; i < 120; i++)
        {
            var sourceX = i - 3 * velocityX;
            var sourceY = j - 3 * velocityY;
            var expected = sourceX > 0 && sourceX < 120 && sourceY > 0 && sourceY < 80
                ? SimulationCases.ValueAt(first, 121, sourceX, sourceY) : 0;
            Assert.Equal(expected, SimulationCases.ValueAt(last, 121, i, j));
        }
    }

    [Fact]
    public void FramesHaveZeroBoundariesNonnegativeValuesAndIndependentStorage()
    {
        var result = solver.Solve(SimulationCases.Demo);
        Assert.Equal(FdmSimulationSolver.Version, result.SolverVersion);
        Assert.Equal(16, result.Frames.Count);
        Assert.Equal(60, result.Frames[^1].TimeSeconds);
        foreach (var frame in result.Frames)
        {
            Assert.Equal(101 * 101 * sizeof(float), frame.Data.Length);
            for (var k = 0; k < 101; k++)
            {
                Assert.Equal(0, SimulationCases.ValueAt(frame, 101, k, 0));
                Assert.Equal(0, SimulationCases.ValueAt(frame, 101, k, 100));
                Assert.Equal(0, SimulationCases.ValueAt(frame, 101, 0, k));
                Assert.Equal(0, SimulationCases.ValueAt(frame, 101, 100, k));
            }
            for (var j = 0; j < 101; j++)
            for (var i = 0; i < 101; i++)
                Assert.InRange(SimulationCases.ValueAt(frame, 101, i, j), 0, 1.000001f);
        }
        Assert.Equal(1, SimulationCases.ValueAt(result.Frames[0], 101, 25, 50));
        Assert.False(result.Frames[0].Data.Span.SequenceEqual(result.Frames[^1].Data.Span));
        Assert.Equal(1, result.Frames[0].MaximumConcentration);
    }

    [Fact]
    public void FrameEncodingIsLittleEndianFloat32()
    {
        var result = solver.Solve(SimulationCases.Demo with { Transport = new(0, 0, 0) });
        var offset = (50 * 101 + 25) * sizeof(float);
        Assert.True(result.Frames[0].Data.Span.Slice(offset, 4).SequenceEqual(new byte[] { 0, 0, 128, 63 }));
    }

    [Fact]
    public void DiffusionMatchesGaussianSolutionAndConvergesOnGridRefinement()
    {
        SimulationResult Run(int nodes)
        {
            var dx = 100.0 / (nodes - 1);
            return solver.Solve(new(new(100, 100, nodes, nodes), new(0, 0, 0.5),
                new(5, 0.1 * dx * dx / 0.5), new(1, 50, 50, 5), RequestedFrames: 2));
        }
        var coarse = SimulationCases.GaussianError(Run(51));
        var medium = SimulationCases.GaussianError(Run(101));
        var fine = SimulationCases.GaussianError(Run(201));
        Assert.True(medium < coarse * 0.6, $"coarse={coarse}, medium={medium}");
        Assert.True(fine < medium * 0.6, $"medium={medium}, fine={fine}");
        Assert.InRange(fine, 0, 0.001);
    }

    [Fact]
    public void MixedAdvectionDiffusionConvergesToTheTranslatedGaussian()
    {
        SimulationResult Run(int nodes) => solver.Solve(new(new(100, 100, nodes, nodes),
            new(0.5, -0.25, 0.1), new(5), new(1, 50, 50, 5), RequestedFrames: 2));
        var coarse = SimulationCases.GaussianError(Run(101));
        var fine = SimulationCases.GaussianError(Run(201));
        Assert.True(fine < coarse, $"coarse={coarse}, fine={fine}");
        Assert.InRange(fine, 0, 0.04);
    }

    [Fact]
    public void DiffusionUsesIndependentSpacingForEachAxis()
    {
        // dx = 0.5, dy = 1; exchanging the x/y coefficients would distort the plume.
        var result = solver.Solve(new(new(120, 80, 241, 81), new(0, 0, 0.5),
            new(5), new(1, 60, 40, 5), RequestedFrames: 2));
        Assert.InRange(SimulationCases.GaussianError(result), 0, 0.003);
    }

    [Fact]
    public void PreCancelledRunDoesNotStartComputation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() =>
            solver.Solve(SimulationCases.Demo, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public void CancellationDuringComputationDoesNotReturnPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new AdvancingClock(TimeSpan.FromMilliseconds(100));
        var reportedSteps = 0;
        Assert.Throws<OperationCanceledException>(() => new FdmSimulationSolver(timeProvider: clock).Solve(
            SimulationCases.Demo, progress =>
            {
                reportedSteps = progress.CompletedSteps;
                if (reportedSteps > 0) cancellation.Cancel();
            }, cancellation.Token));
        Assert.InRange(reportedSteps, 1, 66);
    }

    [Fact]
    public void ExecutionDeadlineIsEnforcedWithoutSleeping()
    {
        var clock = new ManualClock();
        var planner = new SimulationPlanner(SimulationLimits.Default with { MaxExecutionTime = TimeSpan.FromSeconds(1) });
        var error = Assert.Throws<SimulationTimeoutException>(() => new FdmSimulationSolver(planner, clock).Solve(
            SimulationCases.Demo, _ => clock.Advance(TimeSpan.FromSeconds(2))));
        Assert.Equal(TimeSpan.FromSeconds(1), error.Limit);
    }

    [Fact]
    public void ProgressIsThrottledMonotonicAndStopsAt99Percent()
    {
        var clock = new AdvancingClock(TimeSpan.FromMilliseconds(10));
        var reports = new List<(SimulationProgress Progress, long Timestamp)>();
        var result = new FdmSimulationSolver(timeProvider: clock).Solve(SimulationCases.Demo,
            progress => reports.Add((progress, clock.Current)));
        Assert.True(reports.Count > 2);
        Assert.Equal(0, reports[0].Progress.CompletedSteps);
        Assert.Equal(result.Plan.TotalSteps, reports[^1].Progress.CompletedSteps);
        Assert.Equal(99, reports[^1].Progress.Percent);
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Progress.CompletedSteps > reports[i - 1].Progress.CompletedSteps);
            Assert.InRange(reports[i].Progress.Percent, 0, 99);
            if (i < reports.Count - 1)
                Assert.True(reports[i].Timestamp - reports[i - 1].Timestamp >= TimeSpan.FromMilliseconds(500).Ticks);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }

    private sealed class AdvancingClock(TimeSpan increment) : TimeProvider
    {
        public long Current { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Current += increment.Ticks;
    }
}
