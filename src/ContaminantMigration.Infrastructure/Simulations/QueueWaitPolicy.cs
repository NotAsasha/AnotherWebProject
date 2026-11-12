namespace ContaminantMigration.Infrastructure.Simulations;

public sealed record QueuedWork(Guid Id, DateTimeOffset CreatedAt, double CellUpdates);
public sealed record RunningWork(double CellUpdates, double CompletedCellUpdates,
    DateTimeOffset? StartedAt, DateTimeOffset? LeaseUntil, int Attempt);
public sealed record ExecutionSample(double CellUpdates, double Seconds);

public static class QueueWaitPolicy
{
    public static IReadOnlyDictionary<Guid, double?> Estimate(IReadOnlyList<QueuedWork> queued,
        IReadOnlyList<RunningWork> running, IReadOnlyList<ExecutionSample> history,
        int consumers, DateTimeOffset now)
    {
        var result = queued.ToDictionary(x => x.Id, _ => (double?)null);
        // A draining or expired Worker has uncertain availability; do not promise a start time.
        if (consumers <= 0 || running.Count > consumers
            || running.Any(x => x.LeaseUntil is null || x.LeaseUntil <= now)) return result;
        var rates = history.Where(Usable).Select(x => x.Seconds / x.CellUpdates).ToList();
        foreach (var task in running.Where(x => x.Attempt == 1 && x.StartedAt.HasValue))
        {
            var sample = new ExecutionSample(task.CompletedCellUpdates, (now - task.StartedAt!.Value).TotalSeconds);
            if (Usable(sample)) rates.Add(sample.Seconds / sample.CellUpdates);
        }
        rates.Sort();
        double? secondsPerUpdate = rates.Count == 0 ? null : rates[rates.Count / 2];
        var slots = new PriorityQueue<double, double>();
        foreach (var task in running)
        {
            var rate = secondsPerUpdate;
            if (task.Attempt == 1 && task.StartedAt is { } start)
            {
                var sample = new ExecutionSample(task.CompletedCellUpdates, (now - start).TotalSeconds);
                if (Usable(sample)) rate = sample.Seconds / sample.CellUpdates;
            }
            var remaining = Duration(Math.Max(0, task.CellUpdates - task.CompletedCellUpdates), rate);
            slots.Enqueue(remaining, remaining);
        }
        for (var i = running.Count; i < consumers; i++) slots.Enqueue(0, 0);
        // Approximate FIFO. Rabbit delivery, retries and scaling can change the actual order.
        foreach (var task in queued.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id))
        {
            var available = slots.Dequeue();
            result[task.Id] = double.IsFinite(available) ? Math.Ceiling(available) : null;
            var finish = available + Duration(task.CellUpdates, secondsPerUpdate);
            slots.Enqueue(finish, finish);
        }
        return result;
    }

    private static bool Usable(ExecutionSample sample) => double.IsFinite(sample.CellUpdates)
        && sample.CellUpdates >= 1_000_000 && double.IsFinite(sample.Seconds) && sample.Seconds >= 0.05;

    private static double Duration(double updates, double? rate)
        => double.IsFinite(updates) && updates >= 0 && rate.HasValue
            ? updates * rate.Value : double.PositiveInfinity;
}
