using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ContaminantMigration.Infrastructure.Scaling;

public sealed class WorkerScalingHealth : IHealthCheck
{
    private volatile bool ready;
    public void Record(bool healthy) => ready = healthy;
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Worker reconciliation has not succeeded."));
}
