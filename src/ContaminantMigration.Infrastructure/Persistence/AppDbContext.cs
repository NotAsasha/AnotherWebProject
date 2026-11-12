using ContaminantMigration.Core.Entities;
using Microsoft.EntityFrameworkCore;
using MassTransit;

namespace ContaminantMigration.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<SubmissionKey> SubmissionKeys => Set<SubmissionKey>();
    public DbSet<SimulationTask> SimulationTasks => Set<SimulationTask>();
    public DbSet<SimulationResultData> SimulationResults => Set<SimulationResultData>();
    public DbSet<SimulationFrameData> SimulationFrames => Set<SimulationFrameData>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.AddInboxStateEntity();
        model.AddOutboxMessageEntity();
        model.AddOutboxStateEntity();
        var keys = model.Entity<SubmissionKey>();
        keys.HasKey(x => new { x.OwnerId, x.Key });
        keys.HasIndex(x => x.ExpiresAt);
        keys.Property(x => x.RequestHash).HasMaxLength(64);
        var users = model.Entity<AppUser>();
        users.HasKey(x => x.Id);
        users.Property(x => x.Email).HasMaxLength(254).IsRequired();
        users.Property(x => x.NormalizedEmail).HasMaxLength(254).IsRequired();
        users.HasIndex(x => x.NormalizedEmail).IsUnique();
        users.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        users.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
        users.ToTable("Users", table => table.HasCheckConstraint("CK_Users_Role", "\"Role\" IN ('User','Admin')"));

        var tasks = model.Entity<SimulationTask>();
        tasks.HasKey(x => x.Id);
        tasks.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        tasks.HasIndex(x => new { x.OwnerId, x.CreatedAt, x.Id });
        tasks.HasIndex(x => new { x.Status, x.CreatedAt });
        tasks.Property(x => x.Name).HasMaxLength(100).IsRequired();
        tasks.Property(x => x.ParametersJson).HasColumnType("jsonb").IsRequired();
        tasks.Property(x => x.SolverVersion).HasMaxLength(64).IsRequired();
        tasks.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        tasks.Property(x => x.WorkerId).HasMaxLength(128);
        tasks.Property(x => x.ErrorCode).HasMaxLength(64);
        tasks.Property(x => x.Version).IsConcurrencyToken();
        tasks.ToTable("SimulationTasks", table =>
        {
            table.HasCheckConstraint("CK_Tasks_Status", "\"Status\" IN ('Queued','Running','Cancelling','Completed','Cancelled','Failed','TimedOut')");
            table.HasCheckConstraint("CK_Tasks_Progress", "\"ProgressPercent\" >= 0 AND \"ProgressPercent\" <= 100");
            table.HasCheckConstraint("CK_Tasks_Steps", "\"TotalSteps\" > 0 AND \"CompletedSteps\" >= 0 AND \"CompletedSteps\" <= \"TotalSteps\"");
        });

        var results = model.Entity<SimulationResultData>();
        results.HasKey(x => x.TaskId);
        results.HasOne<SimulationTask>().WithOne().HasForeignKey<SimulationResultData>(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        results.Property(x => x.Format).HasMaxLength(32).IsRequired();
        var frames = model.Entity<SimulationFrameData>();
        frames.HasKey(x => new { x.TaskId, x.Index });
        frames.HasOne<SimulationResultData>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        frames.Property(x => x.Data).IsRequired();
    }
}
