using ContaminantMigration.Infrastructure.Scaling;

namespace ContaminantMigration.WebApi.Tests;

public sealed class WorkerScalingTests
{
    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(3, 0, 0, 3)] // Outbox has not delivered commands yet.
    [InlineData(0, 2, 1, 3)]
    [InlineData(2, 2, 1, 3)] // Do not double count DB and broker backlog.
    [InlineData(99, 99, 3, 4)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, 4)]
    public void WorkloadRespectsMinimumMaximumAndOutbox(int queued, int ready, int running, int expected)
        => Assert.Equal(expected, WorkerScalingPolicy.Desired(queued, ready, running, new()));

    [Fact]
    public void InvalidBoundsAndAggressivePollingAreRejected()
    {
        Assert.True(new WorkerScalingOptions().IsValid());
        Assert.False(new WorkerScalingOptions { MinWorkers = 0 }.IsValid());
        Assert.False(new WorkerScalingOptions { MaxWorkers = 17 }.IsValid());
        Assert.False(new WorkerScalingOptions { MinWorkers = 4, MaxWorkers = 3 }.IsValid());
        Assert.False(new WorkerScalingOptions { PollSeconds = 0 }.IsValid());
        Assert.False(new WorkerScalingOptions { PollSeconds = 10, IdleSeconds = 2 }.IsValid());
    }
}
