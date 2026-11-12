using System.Text.Json;
using ContaminantMigration.Contracts.Admin;
using ContaminantMigration.Infrastructure.Simulations;
using StackExchange.Redis;

namespace ContaminantMigration.Infrastructure.Monitoring;

public sealed class MonitoringStore(IConnectionMultiplexer redis)
{
    public async Task Write(string project, ContainerSnapshot snapshot)
        => await redis.GetDatabase().StringSetAsync(Key(project),
            JsonSerializer.Serialize(snapshot, SimulationStorage.Json), TimeSpan.FromSeconds(30));

    public async Task<ContainerSnapshot?> Read(string project)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(Key(project));
            return value.IsNullOrEmpty ? null
                : JsonSerializer.Deserialize<ContainerSnapshot>(value.ToString(), SimulationStorage.Json);
        }
        catch (RedisException) { return null; }
        catch (TimeoutException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string Key(string project) => $"{project}:monitoring:containers";
}
