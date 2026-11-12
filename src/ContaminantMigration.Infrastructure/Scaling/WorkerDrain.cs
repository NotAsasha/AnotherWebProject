using MassTransit;

namespace ContaminantMigration.Infrastructure.Scaling;

public sealed class WorkerDrain(IBusControl bus)
{
    private readonly object gate = new();
    private Task? stopping;
    public bool IsDraining { get { lock (gate) return stopping is not null; } }

    // Stop receiving first, then wait for the active consumer (and its result commit).
    // The operation continues if the HTTP caller disconnects and can be awaited again.
    public Task StopReceiving()
    {
        lock (gate) return stopping ??= bus.StopAsync(CancellationToken.None);
    }
}
