namespace ContaminantMigration.Infrastructure.Configuration;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; init; } = "localhost";
    public ushort Port { get; init; } = 5672;
    public string VirtualHost { get; init; } = "/";
    public string Username { get; init; } = "contaminant";
    public string Password { get; init; } = "";
}
