using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ContaminantMigration.Infrastructure.Health;

internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        command.CommandTimeout = 3;
        await command.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
