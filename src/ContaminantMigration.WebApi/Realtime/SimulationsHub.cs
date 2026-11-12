using System.Security.Claims;
using ContaminantMigration.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ContaminantMigration.WebApi.Realtime;

[Authorize]
public sealed class SimulationsHub(AppDbContext db) : Hub
{
    public static string Group(Guid taskId) => $"simulation:{taskId}";

    public async Task Subscribe(Guid taskId)
    {
        if (!Guid.TryParse(Context.User?.FindFirstValue("sub"), out var owner)) throw new HubException("Unauthorized.");
        var task = await db.SimulationTasks.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == taskId && x.OwnerId == owner, Context.ConnectionAborted);
        if (task is null) throw new HubException("Task not found.");
        if (!Context.Items.TryGetValue("subscriptions", out var value))
            Context.Items["subscriptions"] = value = new HashSet<Guid>();
        var subscriptions = (HashSet<Guid>)value!;
        if (!subscriptions.Contains(taskId) && subscriptions.Count >= 64) throw new HubException("Subscription limit exceeded.");
        subscriptions.Add(taskId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(taskId), Context.ConnectionAborted);
        // Snapshot after joining closes the gap between REST load and the first notification.
        var snapshot = await db.SimulationTasks.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == taskId && x.OwnerId == owner, Context.ConnectionAborted);
        if (snapshot is not null) await Clients.Caller.SendAsync("SimulationUpdated", SimulationNotifier.View(snapshot), Context.ConnectionAborted);
    }

    public async Task Unsubscribe(Guid taskId)
    {
        if (Context.Items.TryGetValue("subscriptions", out var value)) ((HashSet<Guid>)value!).Remove(taskId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(taskId), Context.ConnectionAborted);
    }
}
