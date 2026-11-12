using ContaminantMigration.Infrastructure.Monitoring;
using Docker.DotNet.Models;

namespace ContaminantMigration.WebApi.Tests;

public sealed class ContainerMetricsTests
{
    [Fact]
    public void CpuDeltaSupportsMultipleCoresAndRejectsResetCounters()
    {
        var stats = new ContainerStatsResponse
        {
            CPUStats = new CPUStats { OnlineCPUs = 4, SystemUsage = 2000, CPUUsage = new CPUUsage { TotalUsage = 600 } },
            PreCPUStats = new CPUStats { SystemUsage = 1000, CPUUsage = new CPUUsage { TotalUsage = 100 } }
        };
        Assert.Equal(200d, ContainerMetrics.CpuPercent(stats));
        stats.CPUStats.CPUUsage.TotalUsage = 50;
        Assert.Null(ContainerMetrics.CpuPercent(stats));
        Assert.Null(ContainerMetrics.CpuPercent(new ContainerStatsResponse()));
    }

    [Fact]
    public void MemoryUsesCgroupCacheAndDoesNotUnderflow()
    {
        var stats = new ContainerStatsResponse
        {
            MemoryStats = new MemoryStats { Usage = 1000, Stats = new Dictionary<string, ulong> { ["inactive_file"] = 300 } }
        };
        Assert.Equal(700UL, ContainerMetrics.MemoryBytes(stats));
        stats.MemoryStats.Stats["inactive_file"] = 2000;
        Assert.Equal(1000UL, ContainerMetrics.MemoryBytes(stats));
        Assert.Null(ContainerMetrics.MemoryBytes(new ContainerStatsResponse()));
    }
}
