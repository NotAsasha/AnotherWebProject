using MassTransit;

namespace ContaminantMigration.Infrastructure.Scaling;

public sealed class WorkerDrain(IBusControl bus)
{
    private readonly object gate = new();
    private Task? stopping;
    public bool IsDraining { get { lock (gate) return stopping is not null; } }

    public Task StopReceiving()
    {
        lock (gate) return stopping ??= bus.StopAsync(CancellationToken.None);
    }
}
