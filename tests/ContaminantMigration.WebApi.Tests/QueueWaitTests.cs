using ContaminantMigration.Infrastructure.Simulations;

namespace ContaminantMigration.WebApi.Tests;

public sealed class QueueWaitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static QueuedWork Job(int order, double updates = 10_000_000)
        => new(Guid.NewGuid(), Now.AddSeconds(order), updates);
    private static RunningWork Busy(double completed = 5_000_000, int attempt = 1)
        => new(10_000_000, completed, Now.AddSeconds(-5), Now.AddSeconds(30), attempt);

    [Fact]
    public void DifferentJobSizesAreScheduledOnFirstAvailableWorker()
    {
        var a = Job(1); var b = Job(2, 20_000_000); var c = Job(3);
        var waits = QueueWaitPolicy.Estimate([c, a, b], [Busy()], [new(10_000_000, 10)], 2, Now);
        Assert.Equal(0d, waits[a.Id]);
        Assert.Equal(5d, waits[b.Id]);
        Assert.Equal(10d, waits[c.Id]);
    }

    [Fact]
    public void LiveProgressProvidesEstimateBeforeFirstCompletedTask()
    {
        var job = Job(1);
        var waits = QueueWaitPolicy.Estimate([job], [Busy()], [], 1, Now);
        Assert.Equal(5d, waits[job.Id]);
    }

    [Fact]
    public void ColdStartOnlyPromisesAvailableSlots()
    {
        var a = Job(1); var b = Job(2);
        var waits = QueueWaitPolicy.Estimate([a, b], [], [], 1, Now);
        Assert.Equal(0d, waits[a.Id]);
        Assert.Null(waits[b.Id]);
        Assert.Null(QueueWaitPolicy.Estimate([a], [Busy(0)], [], 1, Now)[a.Id]);
    }

    [Fact]
    public void MissingConsumersExpiredLeasesAndDrainingCapacityReturnUnknown()
    {
        var job = Job(1);
        Assert.Null(QueueWaitPolicy.Estimate([job], [], [new(10_000_000, 10)], 0, Now)[job.Id]);
        Assert.Null(QueueWaitPolicy.Estimate([job], [Busy(), Busy()], [], 1, Now)[job.Id]);
        Assert.Null(QueueWaitPolicy.Estimate([job], [Busy() with { LeaseUntil = Now.AddSeconds(-1) }], [], 1, Now)[job.Id]);
    }

    [Fact]
    public void MedianResistsOutlierAndRetryDoesNotUseOriginalStartTime()
    {
        var job = Job(1);
        var waits = QueueWaitPolicy.Estimate([job], [Busy(attempt: 2)],
            [new(10_000_000, 10), new(10_000_000, 1000), new(10_000_000, 10)], 1, Now);
        Assert.Equal(5d, waits[job.Id]);
    }

    [Fact]
    public void TinyMeasurementsAreNotUsedToExtrapolateHeavyJobs()
    {
        var job = Job(1);
        var waits = QueueWaitPolicy.Estimate([job], [Busy(0)], [new(100, 0.001)], 1, Now);
        Assert.Null(waits[job.Id]);
    }
}
