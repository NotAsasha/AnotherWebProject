using ContaminantMigration.Infrastructure;
using ContaminantMigration.Infrastructure.Health;
using FluentValidation;
using ContaminantMigration.WebApi.Auth;
using ContaminantMigration.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using ContaminantMigration.Core.Simulations;
using System.Text.Json.Serialization;
using ContaminantMigration.WebApi.Realtime;
using MassTransit;
using StackExchange.Redis;
using ContaminantMigration.Infrastructure.Scaling;
using ContaminantMigration.Infrastructure.Simulations;
using ContaminantMigration.Infrastructure.Monitoring;
using ContaminantMigration.Core.Entities;

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

if (builder.Configuration.GetValue<bool>("Scaling:ManagerOnly"))
{
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddOptions<WorkerScalingOptions>().Bind(builder.Configuration.GetSection("Scaling"))
        .Validate(x => x.IsValid(), "Invalid Worker scaling limits.").ValidateOnStart();
    builder.Services.AddSingleton<WorkerScalingHealth>();
    builder.Services.AddHealthChecks().AddCheck<WorkerScalingHealth>("worker-scaling", tags: ["ready"]);
    builder.Services.AddHostedService<WorkerAutoscaler>();
    builder.Services.AddSingleton<MonitoringStore>();
    builder.Services.AddHostedService<ContainerMonitor>();
    var manager = builder.Build();
    manager.MapServiceHealthChecks();
    await manager.RunAsync();
    return;
}

builder.Services.AddProblemDetails();
builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(_ => new SimulationPlanner(
    builder.Configuration.GetSection("SimulationLimits").Get<SimulationLimits>()));
builder.Services.AddOpenApi();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddInfrastructure(builder.Configuration,
    bus => bus.AddConsumer<SimulationNotificationConsumer, SimulationNotificationDefinition>());
builder.Services.AddScoped<SimulationNotifier>();
builder.Services.AddSignalR().AddStackExchangeRedis(builder.Configuration.GetConnectionString("Redis")!, options =>
{
    options.Configuration.ChannelPrefix = RedisChannel.Literal("ContaminantMigration.SignalR");
    options.Configuration.AbortOnConnectFail = false;
    options.Configuration.ConnectTimeout = 3000;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MonitoringStore>();
builder.Services.AddHttpClient<QueueWaitEstimator>(client => client.Timeout = TimeSpan.FromSeconds(2));
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection("Jwt"))
    .Validate(x => x.IsValid(), "Jwt requires a Base64 signing key of at least 32 bytes, issuer, audience and lifetime 1..60 minutes.")
    .ValidateOnStart();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, settings) =>
    {
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = settings.Value.ValidationParameters();
        bearer.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments("/hubs/simulations")
                    && context.Request.Query.TryGetValue("access_token", out var token))
                    context.Token = token.ToString();
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("Admin")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});

var app = builder.Build();
if (builder.Configuration["Admin:PromoteEmail"] is { Length: > 0 } adminEmail)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var normalized = adminEmail.Trim().ToUpperInvariant();
    var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized)
        ?? throw new InvalidOperationException("Register this user before assigning the Admin role.");
    user.Role = UserRole.Admin;
    await db.SaveChangesAsync();
    app.Logger.LogInformation("Admin role assigned to {Email}. Sign in again to refresh the token.", user.Email);
    return;
}
if (builder.Configuration.GetValue<bool>("Database:MigrateOnly"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    return;
}
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Service-Instance"] = Environment.MachineName;
    await next(context);
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<SimulationsHub>("/hubs/simulations", options => options.CloseOnAuthenticationExpiration = true);
app.MapServiceHealthChecks();
app.Run();
