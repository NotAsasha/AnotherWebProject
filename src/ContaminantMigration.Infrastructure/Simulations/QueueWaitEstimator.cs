using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContaminantMigration.Core.Entities;
using ContaminantMigration.Core.Simulations;
using ContaminantMigration.Infrastructure.Configuration;
using ContaminantMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContaminantMigration.Infrastructure.Simulations;

public sealed class QueueWaitEstimator(AppDbContext db, HttpClient http,
    IOptions<RabbitMqOptions> settings, TimeProvider clock)
{
    public async Task<IReadOnlyDictionary<Guid, double?>> Estimate(CancellationToken ct)
    {
        var active = await db.SimulationTasks.AsNoTracking()
            .Where(x => SimulationStorage.Active.Contains(x.Status)).ToListAsync(ct);
        var queued = active.Where(x => x.Status == SimulationStatus.Queued)
            .Select(x => new QueuedWork(x.Id, x.CreatedAt, Work(x))).ToArray();
        if (queued.Length == 0) return new Dictionary<Guid, double?>();
        var unknown = queued.ToDictionary(x => x.Id, _ => (double?)null);
        var now = clock.GetUtcNow();
        var since = now.AddHours(-12);
        var recent = await db.SimulationTasks.AsNoTracking()
            .Where(x => x.Status == SimulationStatus.Completed && x.Attempt == 1
                && x.SolverVersion == FdmSimulationSolver.Version && x.FinishedAt >= since && x.StartedAt != null)
            .OrderByDescending(x => x.FinishedAt).Take(20).ToListAsync(ct);
        var history = recent.Select(x => new ExecutionSample(Work(x),
            (x.FinishedAt!.Value - x.StartedAt!.Value).TotalSeconds)).ToArray();
        var running = active.Where(x => x.Status != SimulationStatus.Queued).Select(x =>
            new RunningWork(Work(x), Work(x) * x.CompletedSteps / x.TotalSteps,
                x.StartedAt, x.LeaseUntil, x.Attempt)).ToArray();
        try
        {
            var rabbit = settings.Value;
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"http://{rabbit.Host}:{rabbit.ManagementPort}/api/queues/{Uri.EscapeDataString(rabbit.VirtualHost)}/simulation");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rabbit.Username}:{rabbit.Password}")));
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return unknown;
            response.EnsureSuccessStatusCode();
            var metrics = await response.Content.ReadFromJsonAsync<QueueMetrics>(ct);
            if (metrics?.consumers is not { } consumers || consumers < 0) return unknown;
            return QueueWaitPolicy.Estimate(queued, running, history,
                Math.Min(consumers, SimulationStorage.MaxActiveSystem), now);
        }
        catch (HttpRequestException) { return unknown; }
        catch (JsonException) { return unknown; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return unknown; }
    }

    private static double Work(SimulationTask task)
    {
        try
        {
            var parameters = JsonSerializer.Deserialize<SimulationParameters>(task.ParametersJson, SimulationStorage.Json);
            return parameters is null ? double.NaN
                : (double)parameters.Domain.NodesX * parameters.Domain.NodesY * task.TotalSteps;
        }
        catch (JsonException) { return double.NaN; }
    }

    private sealed record QueueMetrics(int? consumers);
}
