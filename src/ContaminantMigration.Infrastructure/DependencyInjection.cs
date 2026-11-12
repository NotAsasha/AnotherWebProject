using ContaminantMigration.Infrastructure.Configuration;
using ContaminantMigration.Infrastructure.Health;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;
using ContaminantMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContaminantMigration.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureBus = null)
    {
        var postgres = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(postgres))
            throw new InvalidOperationException("Configure ConnectionStrings:Postgres before starting the service.");

        var postgresOptions = new NpgsqlConnectionStringBuilder(postgres);
        if (string.IsNullOrWhiteSpace(postgresOptions.Host)
            || string.IsNullOrWhiteSpace(postgresOptions.Database)
            || string.IsNullOrWhiteSpace(postgresOptions.Username)
            || string.IsNullOrWhiteSpace(postgresOptions.Password))
            throw new InvalidOperationException("Postgres requires Host, Database, Username and Password.");

        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis))
            throw new InvalidOperationException("Configure ConnectionStrings:Redis before starting the service.");

        services.AddSingleton(_ => NpgsqlDataSource.Create(postgres));
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(postgres));
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redis);
            options.AbortOnConnectFail = false;
            options.ConnectRetry = 0;
            options.ConnectTimeout = 3000;
            options.AsyncTimeout = 3000;
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .Validate(x => !string.IsNullOrWhiteSpace(x.Host) && x.Port > 0,
                "RabbitMq requires Host and a valid Port.")
            .Validate(x => !string.IsNullOrWhiteSpace(x.VirtualHost)
                && !string.IsNullOrWhiteSpace(x.Username)
                && !string.IsNullOrWhiteSpace(x.Password),
                "RabbitMq requires VirtualHost, Username and Password.")
            .ValidateOnStart();

        services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();
            bus.AddEntityFrameworkOutbox<AppDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
            });
            configureBus?.Invoke(bus);
            bus.UsingRabbitMq((context, transport) =>
            {
                var options = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
                transport.Host(options.Host, options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username);
                    host.Password(options.Password);
                });
                transport.ConfigureEndpoints(context);
            });
        });

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(5))
            .AddCheck<RedisHealthCheck>("redis", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

        return services;
    }
}
