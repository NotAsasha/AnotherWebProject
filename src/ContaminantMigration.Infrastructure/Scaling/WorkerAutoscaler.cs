using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Docker.DotNet;
using Docker.DotNet.Models;
using ContaminantMigration.Core.Entities;
using ContaminantMigration.Infrastructure.Configuration;
using ContaminantMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ContaminantMigration.Infrastructure.Scaling;

public sealed class WorkerAutoscaler(IServiceScopeFactory scopes, NpgsqlDataSource postgres,
    IOptions<WorkerScalingOptions> settings, IOptions<RabbitMqOptions> rabbitSettings,
    WorkerScalingHealth health, ILogger<WorkerAutoscaler> logger) : BackgroundService
{
    private const string ManagedLabel = "contaminantmigration.autoscaler";
    private readonly WorkerScalingOptions options = settings.Value;
    private readonly RabbitMqOptions rabbit = rabbitSettings.Value;
    private DateTimeOffset lastBusy = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var docker = new DockerClientConfiguration(new Uri(options.DockerEndpoint)).CreateClient();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        do
        {
            try
            {
                // Session advisory lock also serializes two accidentally started managers.
                await using var connection = await postgres.OpenConnectionAsync(stoppingToken);
                await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(71420932)", connection);
                if ((bool)(await claim.ExecuteScalarAsync(stoppingToken))!)
                {
                    try { await Reconcile(docker, http, stoppingToken); health.Record(true); }
                    finally
                    {
                        await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(71420932)", connection);
                        await release.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                health.Record(false);
                logger.LogError(error, "Worker scaling check failed; keeping existing workers");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task Reconcile(IDockerClient docker, HttpClient http, CancellationToken ct)
    {
        var all = await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
        var baseline = all.Where(x => Label(x, "com.docker.compose.project") == options.Project
            && Label(x, "com.docker.compose.service") == "worker" && x.State == "running").ToList();
        if (baseline.Count == 0) throw new InvalidOperationException("No running Compose Worker template found.");
        var managed = all.Where(x => Label(x, ManagedLabel) == options.Project).ToList();
        foreach (var exited in managed.Where(x => x.State is "exited" or "created" or "dead"))
        {
            await docker.Containers.RemoveContainerAsync(exited.ID, new ContainerRemoveParameters(), ct);
        }
        // Restarting/paused containers still consume a slot in the hard upper bound.
        var live = managed.Where(x => x.State is "running" or "restarting" or "paused").ToList();
        // Resume a drain left in progress by a manager restart.
        foreach (var worker in live.Where(x => x.State == "running"))
        {
            var details = await docker.Containers.InspectContainerAsync(worker.ID, ct);
            if (await IsDraining(http, details, ct))
            {
                await DrainAndRemove(docker, http, details, ct);
                return;
            }
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queued = await db.SimulationTasks.CountAsync(x => x.Status == SimulationStatus.Queued, ct);
        var running = await db.SimulationTasks.CountAsync(x => x.Status == SimulationStatus.Running || x.Status == SimulationStatus.Cancelling, ct);
        var ready = await QueueDepth(http, ct); // Fail closed if Rabbit management is unavailable.
        var desired = WorkerScalingPolicy.Desired(queued, ready, running, options);
        var current = baseline.Count + live.Count;
        if (queued + running + ready > 0) lastBusy = DateTimeOffset.UtcNow;
        if (desired > current)
        {
            var template = await docker.Containers.InspectContainerAsync(baseline[0].ID, ct);
            await CreateWorker(docker, template, ct);
        }
        else if (desired < current && live.Any(x => x.State == "running")
            && DateTimeOffset.UtcNow - lastBusy >= TimeSpan.FromSeconds(options.IdleSeconds))
        {
            var worker = await docker.Containers.InspectContainerAsync(live.Last(x => x.State == "running").ID, ct);
            await DrainAndRemove(docker, http, worker, ct);
        }
    }

    private async Task<int> QueueDepth(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://{rabbit.Host}:{options.RabbitManagementPort}/api/queues/{Uri.EscapeDataString(rabbit.VirtualHost)}/simulation");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{rabbit.Username}:{rabbit.Password}")));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return 0;
        response.EnsureSuccessStatusCode();
        var queue = await response.Content.ReadFromJsonAsync<QueueInfo>(ct);
        return queue?.messages_ready ?? throw new InvalidOperationException("Missing Rabbit queue metrics.");
    }

    private async Task CreateWorker(IDockerClient docker, ContainerInspectResponse template, CancellationToken ct)
    {
        if (template.Mounts.Count > 0) throw new InvalidOperationException("Worker template must not have host mounts.");
        var name = $"yet-another-worker-{Guid.NewGuid():N}";
        var network = template.NetworkSettings.Networks.Keys.Single();
        var environment = template.Config.Env.Where(x => !x.StartsWith("WorkerManagement__Token=", StringComparison.OrdinalIgnoreCase)).ToList();
        environment.Add($"WorkerManagement__Token={Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}");
        var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name, Hostname = name, Image = template.Image, User = template.Config.User,
            Env = environment, Entrypoint = template.Config.Entrypoint, Cmd = template.Config.Cmd,
            WorkingDir = template.Config.WorkingDir, Healthcheck = template.Config.Healthcheck,
            Labels = new Dictionary<string, string> { [ManagedLabel] = options.Project },
            HostConfig = new HostConfig
            {
                NetworkMode = network, Memory = options.WorkerMemoryBytes, NanoCPUs = options.WorkerNanoCpus,
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
            },
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings> { [network] = new() { Aliases = [name] } }
            }
        }, ct);
        if (!await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct))
            throw new InvalidOperationException("Docker did not start the managed Worker.");
        logger.LogInformation("Scaled up: created Worker {Worker}", name);
    }

    private static string ManagementUrl(ContainerInspectResponse container)
        => $"http://{container.NetworkSettings.Networks.Values.Single().IPAddress}:8080/internal/worker";

    private static HttpRequestMessage ManagementRequest(ContainerInspectResponse container, HttpMethod method)
    {
        var token = container.Config.Env.Single(x => x.StartsWith("WorkerManagement__Token=", StringComparison.Ordinal));
        var request = new HttpRequestMessage(method, ManagementUrl(container));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Split('=', 2)[1]);
        return request;
    }

    private static async Task<bool> IsDraining(HttpClient http, ContainerInspectResponse worker, CancellationToken ct)
    {
        using var request = ManagementRequest(worker, HttpMethod.Get);
        try
        {
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<DrainInfo>(ct))!.Draining;
        }
        catch (HttpRequestException) { return false; } // Newly started Worker may not listen yet.
    }

    private async Task DrainAndRemove(IDockerClient docker, HttpClient http, ContainerInspectResponse worker, CancellationToken ct)
    {
        using var request = ManagementRequest(worker, HttpMethod.Post);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode(); // Never stop/kill on a drain timeout or failure.
        await docker.Containers.StopContainerAsync(worker.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 30 }, ct);
        await docker.Containers.RemoveContainerAsync(worker.ID, new ContainerRemoveParameters(), ct);
        logger.LogInformation("Scaled down: drained and removed Worker {Worker}", worker.Name);
    }

    private sealed record QueueInfo(int messages_ready);
    private sealed record DrainInfo(bool Draining);
    private static string? Label(ContainerListResponse container, string key)
        => container.Labels.TryGetValue(key, out var value) ? value : null;
}
