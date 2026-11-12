using ContaminantMigration.Contracts.Simulations;
using ContaminantMigration.Core.Entities;
using ContaminantMigration.Infrastructure.Persistence;
using ContaminantMigration.Infrastructure.Simulations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace ContaminantMigration.Worker;

public sealed class TaskRecoveryService(IServiceScopeFactory scopes, ILogger<TaskRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                var now = DateTimeOffset.UtcNow;
                var tasks = await db.SimulationTasks.FromSqlInterpolated($"SELECT * FROM \"SimulationTasks\" WHERE \"Status\" IN ('Running','Cancelling') AND \"LeaseUntil\" <= {now} ORDER BY \"LeaseUntil\" LIMIT 20 FOR UPDATE SKIP LOCKED").ToListAsync(stoppingToken);
                foreach (var task in tasks)
                {
                    task.Version++;
                    task.LeaseUntil = null;
                    task.WorkerId = null;
                    if (task.Status == SimulationStatus.Cancelling)
                    { task.Status = SimulationStatus.Cancelled; task.FinishedAt = now; }
                    else if (task.Attempt >= SimulationStorage.MaxAttempts)
                    { task.Status = SimulationStatus.Failed; task.ErrorCode = "WorkerLeaseExpired"; task.FinishedAt = now; }
                    else
                    {
                        task.Status = SimulationStatus.Queued;
                        task.CompletedSteps = 0;
                        task.ProgressPercent = 0;
                        var endpoint = await scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>().GetSendEndpoint(new Uri("queue:simulation"));
                        await endpoint.Send(new RunSimulationCommand(task.Id), stoppingToken);
                    }
                    if (task.Status != SimulationStatus.Queued)
                        await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(new SimulationFinishedEvent(
                            task.Id, task.Version, task.Attempt, task.Status.ToString(), task.ErrorCode, now), stoppingToken);
                }
                await db.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
                // Expired keys no longer reserve a request identity.
                await using var cleanup = await db.Database.BeginTransactionAsync(stoppingToken);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(71420931)", stoppingToken);
                await db.SubmissionKeys.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(stoppingToken);
                await cleanup.CommitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogError(error, "Could not recover expired task leases; will retry."); }
        }
    }
}
