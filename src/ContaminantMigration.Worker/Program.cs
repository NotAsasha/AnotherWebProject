using ContaminantMigration.Infrastructure;
using ContaminantMigration.Infrastructure.Health;
using Microsoft.AspNetCore.Builder;
using ContaminantMigration.Worker;
using ContaminantMigration.Core.Simulations;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "O";
});
builder.Services.AddSingleton(_ => new SimulationPlanner(
    builder.Configuration.GetSection("SimulationLimits").Get<SimulationLimits>()));
builder.Services.AddSingleton<ISimulationSolver, FdmSimulationSolver>();
builder.Services.AddInfrastructure(builder.Configuration, bus => bus.AddConsumer<SimulationConsumer, SimulationConsumerDefinition>());
builder.Services.AddHostedService<TaskRecoveryService>();

var app = builder.Build();
app.MapServiceHealthChecks();
await app.RunAsync();
