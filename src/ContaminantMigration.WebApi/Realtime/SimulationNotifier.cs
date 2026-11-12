using ContaminantMigration.Contracts.Simulations;
using ContaminantMigration.Core.Entities;
using ContaminantMigration.Infrastructure.Persistence;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ContaminantMigration.WebApi.Realtime;

public sealed class SimulationNotifier(AppDbContext db, IHubContext<SimulationsHub> hub)
{
    public static SimulationUpdated View(SimulationTask task) => new(task.Id, task.Version, task.Status.ToString(),
        task.ProgressPercent, task.CompletedSteps, task.TotalSteps, task.Attempt, task.ErrorCode);

    public async Task Notify(Guid id, CancellationToken ct)
    {
        var task = await db.SimulationTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (task is not null) await hub.Clients.Group(SimulationsHub.Group(id)).SendAsync("SimulationUpdated", View(task), ct);
    }
}

public sealed class SimulationNotificationConsumer(SimulationNotifier notifier) :
    IConsumer<SimulationProgressEvent>, IConsumer<SimulationFinishedEvent>, IConsumer<SimulationStateChangedEvent>
{
    public Task Consume(ConsumeContext<SimulationProgressEvent> context) => notifier.Notify(context.Message.TaskId, context.CancellationToken);
    public Task Consume(ConsumeContext<SimulationFinishedEvent> context) => notifier.Notify(context.Message.TaskId, context.CancellationToken);
    public Task Consume(ConsumeContext<SimulationStateChangedEvent> context) => notifier.Notify(context.Message.TaskId, context.CancellationToken);
}

public sealed class SimulationNotificationDefinition : ConsumerDefinition<SimulationNotificationConsumer>
{
    public SimulationNotificationDefinition()
    {
        EndpointName = "simulation-notifications";
        ConcurrentMessageLimit = 8;
    }
}
