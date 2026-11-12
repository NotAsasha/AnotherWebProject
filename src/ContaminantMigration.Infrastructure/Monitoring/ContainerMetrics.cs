using Docker.DotNet.Models;

namespace ContaminantMigration.Infrastructure.Monitoring;

public static class ContainerMetrics
{
    public static double? CpuPercent(ContainerStatsResponse stats)
    {
        var current = stats.CPUStats;
        var previous = stats.PreCPUStats;
        if (current?.CPUUsage is null || previous?.CPUUsage is null
            || current.SystemUsage <= previous.SystemUsage
            || current.CPUUsage.TotalUsage < previous.CPUUsage.TotalUsage) return null;
        var cores = current.OnlineCPUs > 0 ? current.OnlineCPUs : (ulong)(current.CPUUsage.PercpuUsage?.Count ?? 0);
        if (cores == 0) return null;
        return Math.Round((double)(current.CPUUsage.TotalUsage - previous.CPUUsage.TotalUsage)
            / (current.SystemUsage - previous.SystemUsage) * cores * 100, 2);
    }

    public static ulong? MemoryBytes(ContainerStatsResponse stats)
    {
        if (stats.MemoryStats is null) return null;
        var memory = stats.MemoryStats;
        ulong cache = 0;
        if (memory.Stats is not null)
        {
            if (!memory.Stats.TryGetValue("inactive_file", out cache)
                && !memory.Stats.TryGetValue("total_inactive_file", out cache))
                memory.Stats.TryGetValue("cache", out cache);
        }
        return memory.Usage >= cache ? memory.Usage - cache : memory.Usage;
    }
}
