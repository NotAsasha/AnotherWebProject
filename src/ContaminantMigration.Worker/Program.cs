using ContaminantMigration.Infrastructure;
using ContaminantMigration.Infrastructure.Health;
using Microsoft.AspNetCore.Builder;
using ContaminantMigration.Worker;
using ContaminantMigration.Core.Simulations;
using MassTransit;
using ContaminantMigration.Infrastructure.Scaling;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;

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
builder.Services.AddSingleton<WorkerDrain>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(90));

var app = builder.Build();
app.MapServiceHealthChecks();
app.MapMethods("/internal/worker", ["GET", "POST"], async (HttpContext context, WorkerDrain drain) =>
{
    var secret = builder.Configuration["WorkerManagement:Token"];
    if (string.IsNullOrEmpty(secret)) return Results.NotFound();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes($"Bearer {secret}"),
        Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()))) return Results.Unauthorized();
    if (HttpMethods.IsPost(context.Request.Method)) await drain.StopReceiving();
    return Results.Ok(new { draining = drain.IsDraining });
});
await app.RunAsync();
