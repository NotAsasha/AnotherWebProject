using System.Text.Json;
using ContaminantMigration.Contracts.Simulations;
using ContaminantMigration.Core.Entities;
using ContaminantMigration.Core.Simulations;
using ContaminantMigration.Infrastructure.Persistence;
using ContaminantMigration.Infrastructure.Simulations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace ContaminantMigration.Worker;

public sealed class SimulationConsumerDefinition : ConsumerDefinition<SimulationConsumer>
{
    public SimulationConsumerDefinition()
    {
        EndpointName = "simulation";
        ConcurrentMessageLimit = 1;
    }
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<SimulationConsumer> consumer, IRegistrationContext context) => endpoint.PrefetchCount = 1;
}

public sealed class SimulationConsumer(AppDbContext db,
    IServiceScopeFactory scopes, ISimulationSolver solver) : IConsumer<RunSimulationCommand>
{
    public async Task Consume(ConsumeContext<RunSimulationCommand> context)
    {
        var id = context.Message.TaskId;
        var now = DateTimeOffset.UtcNow;
        var workerId = Environment.MachineName + ":" + Guid.NewGuid().ToString("N");
        var claimed = await db.SimulationTasks.Where(x => x.Id == id && x.Status == SimulationStatus.Queued
            && x.Attempt < SimulationStorage.MaxAttempts).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.Status, SimulationStatus.Running).SetProperty(x => x.WorkerId, workerId)
            .SetProperty(x => x.Attempt, x => x.Attempt + 1).SetProperty(x => x.Version, x => x.Version + 1)
            .SetProperty(x => x.StartedAt, x => x.StartedAt ?? now)
            .SetProperty(x => x.LeaseUntil, now + SimulationStorage.LeaseDuration), context.CancellationToken);
        if (claimed == 0) return; // Duplicate, deleted or already cancelled message.
        var task = await db.SimulationTasks.AsNoTracking().SingleAsync(x => x.Id == id, context.CancellationToken);
        using var computation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        SimulationProgress? progress = null;
        var monitor = Monitor(id, workerId, task.Attempt, () => Volatile.Read(ref progress), computation, monitorStop.Token);
        SimulationResult? result = null;
        var status = SimulationStatus.Completed;
        string? errorCode = null;
        try
        {
            if (task.SolverVersion != FdmSimulationSolver.Version)
                throw new SimulationValidationException([new("solverVersion", "Unsupported", "Unsupported solver version.")]);
            var parameters = JsonSerializer.Deserialize<SimulationParameters>(task.ParametersJson, SimulationStorage.Json)
                ?? throw new JsonException("Missing simulation parameters.");
            result = await Task.Run(() => solver.Solve(parameters, value => Volatile.Write(ref progress, value), computation.Token));
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        { status = SimulationStatus.Cancelled; }
        catch (SimulationTimeoutException) { status = SimulationStatus.TimedOut; errorCode = "ExecutionTimeout"; }
        catch (SimulationNumericalException) { status = SimulationStatus.Failed; errorCode = "NumericalFailure"; }
        catch (Exception exception) when (exception is SimulationValidationException or JsonException)
        { status = SimulationStatus.Failed; errorCode = "InvalidStoredParameters"; }
        finally
        {
            monitorStop.Cancel();
            try { await monitor; }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
        }

        await using var finalScope = scopes.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var publisher = finalScope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        await using var transaction = await finalDb.Database.BeginTransactionAsync(context.CancellationToken);
        var locked = await finalDb.SimulationTasks.FromSqlInterpolated($"SELECT * FROM \"SimulationTasks\" WHERE \"Id\" = {id} FOR UPDATE")
            .ToListAsync(context.CancellationToken);
        var current = locked.SingleOrDefault();
        if (current is null || current.WorkerId != workerId || current.Attempt != task.Attempt
            || current.LeaseUntil <= DateTimeOffset.UtcNow || current.Status is not (SimulationStatus.Running or SimulationStatus.Cancelling)) return;
        if (current.Status == SimulationStatus.Cancelling) { status = SimulationStatus.Cancelled; errorCode = null; }
        current.Status = status;
        current.ErrorCode = errorCode;
        current.FinishedAt = DateTimeOffset.UtcNow;
        current.LeaseUntil = null;
        current.Version++;
        if (status == SimulationStatus.Completed && result is not null)
        {
            current.ProgressPercent = 100;
            current.CompletedSteps = current.TotalSteps;
            finalDb.SimulationResults.Add(new SimulationResultData { TaskId = id,
                NodesX = result.Plan.Parameters.Domain.NodesX, NodesY = result.Plan.Parameters.Domain.NodesY });
            for (var i = 0; i < result.Frames.Count; i++)
            {
                var frame = result.Frames[i];
                finalDb.SimulationFrames.Add(new SimulationFrameData { TaskId = id, Index = i, Step = frame.Step,
                    TimeSeconds = frame.TimeSeconds, MinimumConcentration = frame.MinimumConcentration,
                    MaximumConcentration = frame.MaximumConcentration, Data = frame.Data.ToArray() });
            }
        }
        await publisher.Publish(new SimulationFinishedEvent(id, current.Version, current.Attempt,
            status.ToString(), errorCode, DateTimeOffset.UtcNow), context.CancellationToken);
        await finalDb.SaveChangesAsync(context.CancellationToken);
        await transaction.CommitAsync(context.CancellationToken);
    }

    private async Task Monitor(Guid id, string workerId, int attempt, Func<SimulationProgress?> getProgress,
        CancellationTokenSource computation, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var lastHeartbeat = DateTimeOffset.UtcNow;
        var lastStep = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await using var scope = scopes.CreateAsyncScope();
                var stateDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var task = await stateDb.SimulationTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
                var now = DateTimeOffset.UtcNow;
                if (task is null || task.WorkerId != workerId || task.Attempt != attempt
                    || task.Status != SimulationStatus.Running || task.LeaseUntil <= now)
                { computation.Cancel(); return; }
                var progress = getProgress();
                var heartbeat = now - lastHeartbeat >= TimeSpan.FromSeconds(5);
                if (!heartbeat && (progress is null || progress.CompletedSteps <= lastStep)) continue;
                await using var transaction = await stateDb.Database.BeginTransactionAsync(ct);
                var changed = await stateDb.SimulationTasks.Where(x => x.Id == id && x.Version == task.Version
                    && x.WorkerId == workerId && x.Status == SimulationStatus.Running && x.LeaseUntil > now)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseUntil, now + SimulationStorage.LeaseDuration)
                        .SetProperty(x => x.Version, x => x.Version + 1)
                        .SetProperty(x => x.CompletedSteps, progress == null ? task.CompletedSteps : progress.CompletedSteps)
                        .SetProperty(x => x.ProgressPercent, progress == null ? task.ProgressPercent : progress.Percent), ct);
                if (changed == 0) continue;
                if (progress is not null && progress.CompletedSteps > lastStep)
                {
                    await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(
                        new SimulationProgressEvent(id, task.Version + 1, attempt, progress.CompletedSteps,
                            task.TotalSteps, progress.Percent, now), ct);
                    await stateDb.SaveChangesAsync(ct);
                    lastStep = progress.CompletedSteps;
                }
                await transaction.CommitAsync(ct);
                lastHeartbeat = now;
            }
        }
        catch { computation.Cancel(); throw; }
    }
}
