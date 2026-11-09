using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace ContaminantMigration.Infrastructure.Health;

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
