namespace ContaminantMigration.Infrastructure.Scaling;

public sealed class WorkerScalingOptions
{
    public string Project { get; set; } = "contaminantmigration";
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";
    public int MinWorkers { get; set; } = 1;
    public int MaxWorkers { get; set; } = 4;
    public int PollSeconds { get; set; } = 2;
    public int IdleSeconds { get; set; } = 30;
    public int RabbitManagementPort { get; set; } = 15672;
    public long WorkerMemoryBytes { get; set; } = 512L * 1024 * 1024;
    public long WorkerNanoCpus { get; set; } = 1_000_000_000;

    public bool IsValid() => !string.IsNullOrWhiteSpace(Project)
        && Uri.TryCreate(DockerEndpoint, UriKind.Absolute, out _)
        && MinWorkers >= 1 && MaxWorkers >= MinWorkers && MaxWorkers <= 16
        && PollSeconds >= 1 && IdleSeconds >= PollSeconds
        && RabbitManagementPort is > 0 and <= 65535
        && WorkerMemoryBytes >= 128L * 1024 * 1024 && WorkerNanoCpus > 0;
}

public static class WorkerScalingPolicy
{
    // DB queued tasks include commands still waiting in the transactional outbox.
    public static int Desired(int databaseQueued, int brokerReady, int running, WorkerScalingOptions options)
        => (int)Math.Clamp((long)Math.Max(databaseQueued, brokerReady) + running,
            options.MinWorkers, options.MaxWorkers);
}
