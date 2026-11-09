using ContaminantMigration.Infrastructure;
using ContaminantMigration.Infrastructure.Health;
using Microsoft.AspNetCore.Builder;

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
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.MapServiceHealthChecks();
await app.RunAsync();
