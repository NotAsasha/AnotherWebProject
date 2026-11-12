namespace ContaminantMigration.Contracts.Admin;

public sealed record ContainerMetric(string Id, string Name, string Service, string State,
    string? Health, double? CpuPercent, ulong? MemoryBytes, ulong? MemoryLimitBytes, bool Managed);
public sealed record ScalingOverview(int MinWorkers, int MaxWorkers, int CurrentWorkers,
    int PollSeconds, int IdleSeconds);
public sealed record ContainerSnapshot(DateTimeOffset CollectedAtUtc,
    IReadOnlyList<ContainerMetric> Containers, ScalingOverview Scaling);
public sealed record AdminTask(Guid Id, string Name, string Status, double ProgressPercent,
    DateTimeOffset CreatedAt, string? WorkerId);
public sealed record AdminOverview(DateTimeOffset AsOfUtc, bool MetricsStale,
    ContainerSnapshot? Monitoring, IReadOnlyDictionary<string, int> Tasks, IReadOnlyList<AdminTask> ActiveTasks);
