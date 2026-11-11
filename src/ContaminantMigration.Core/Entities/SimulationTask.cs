namespace ContaminantMigration.Core.Entities;

public enum SimulationStatus { Queued, Running, Cancelling, Completed, Cancelled, Failed, TimedOut }

public sealed class SimulationTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public string SolverVersion { get; set; } = "";
    public SimulationStatus Status { get; set; } = SimulationStatus.Queued;
    public int CompletedSteps { get; set; }
    public int TotalSteps { get; set; }
    public double ProgressPercent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public string? WorkerId { get; set; }
    public int Attempt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long Version { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class SimulationResultData
{
    public Guid TaskId { get; set; }
    public int NodesX { get; set; }
    public int NodesY { get; set; }
    public string Format { get; set; } = "float32-le";
}

public sealed class SimulationFrameData
{
    public Guid TaskId { get; set; }
    public int Index { get; set; }
    public int Step { get; set; }
    public double TimeSeconds { get; set; }
    public float MinimumConcentration { get; set; }
    public float MaximumConcentration { get; set; }
    public byte[] Data { get; set; } = [];
}
