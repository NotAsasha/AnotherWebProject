using System.Text.Json;
using System.Text.Json.Serialization;
using ContaminantMigration.Core.Entities;

namespace ContaminantMigration.Infrastructure.Simulations;

public static class SimulationStorage
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public static readonly SimulationStatus[] Active = [SimulationStatus.Queued, SimulationStatus.Running, SimulationStatus.Cancelling];
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    public const int MaxAttempts = 2;
    public const int MaxActivePerUser = 3;
    public const int MaxTasksPerUser = 100;
    public const int MaxActiveSystem = 100;
}
