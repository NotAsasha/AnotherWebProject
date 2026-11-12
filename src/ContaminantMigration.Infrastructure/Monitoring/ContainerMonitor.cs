using System.Collections.Concurrent;
using ContaminantMigration.Contracts.Admin;
using ContaminantMigration.Infrastructure.Scaling;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContaminantMigration.Infrastructure.Monitoring;

public sealed class ContainerMonitor(IOptions<WorkerScalingOptions> settings, MonitoringStore store,
    ILogger<ContainerMonitor> logger) : BackgroundService
{
    private readonly WorkerScalingOptions options = settings.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var docker = new DockerClientConfiguration(new Uri(options.DockerEndpoint)).CreateClient();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                var containers = await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true }, stoppingToken);
                var owned = containers.Where(x => Label(x, "com.docker.compose.project") == options.Project
                    || Label(x, "contaminantmigration.autoscaler") == options.Project).ToArray();
                var metrics = new ConcurrentBag<ContainerMetric>();
                await Parallel.ForEachAsync(owned, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken },
                    async (container, ct) =>
                    {
                        var capture = new StatsCapture();
                        if (container.State == "running")
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            timeout.CancelAfter(TimeSpan.FromSeconds(4));
                            try
                            {
                                await docker.Containers.GetContainerStatsAsync(container.ID,
                                    new ContainerStatsParameters { Stream = false }, capture, timeout.Token);
                            }
                            catch (DockerApiException) { /* Container can disappear while being sampled. */ }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                        }
                        var stats = capture.Value;
                        var managed = Label(container, "contaminantmigration.autoscaler") == options.Project;
                        var health = container.Status.Contains("(unhealthy)") ? "unhealthy"
                            : container.Status.Contains("(healthy)") ? "healthy"
                            : container.Status.Contains("health: starting") ? "starting" : null;
                        metrics.Add(new(container.ID[..12], container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12],
                            managed ? "worker" : Label(container, "com.docker.compose.service") ?? "unknown",
                            container.State, health, stats is null ? null : ContainerMetrics.CpuPercent(stats),
                            stats is null ? null : ContainerMetrics.MemoryBytes(stats), stats?.MemoryStats?.Limit, managed));
                    });
                var currentWorkers = metrics.Count(x => x.Service == "worker" && x.State is "running" or "restarting" or "paused");
                await store.Write(options.Project, new(DateTimeOffset.UtcNow,
                    metrics.OrderBy(x => x.Service).ThenBy(x => x.Name).ToArray(),
                    new(options.MinWorkers, options.MaxWorkers, currentWorkers, options.PollSeconds, options.IdleSeconds)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Could not update container monitoring snapshot"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static string? Label(ContainerListResponse container, string key)
        => container.Labels.TryGetValue(key, out var value) ? value : null;
    private sealed class StatsCapture : IProgress<ContainerStatsResponse>
    {
        public ContainerStatsResponse? Value { get; private set; }
        public void Report(ContainerStatsResponse value) => Value = value;
    }
}
