using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ContaminantMigration.Infrastructure.Persistence;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ContaminantMigration.slnx"))) root = root.Parent;
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root?.FullName ?? Directory.GetCurrentDirectory(), "src", "ContaminantMigration.WebApi", "appsettings.Local.json"), optional: true)
            .AddEnvironmentVariables().Build();
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Configure ConnectionStrings__Postgres or run scripts/Configure-Local.ps1 from the repository root.");
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
    }
}
