using Devage.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Devage.Persistence;

public sealed class DevageDbContext : DbContext
{
    public DevageDbContext(DbContextOptions<DevageDbContext> options) : base(options)
    {
    }

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<ToolBinding> ToolBindings => Set<ToolBinding>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanStep> PlanSteps => Set<PlanStep>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<ActionLog> ActionLogs => Set<ActionLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Agent>(e =>
        {
            e.ToTable("agents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.WorkspaceRoot).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Goal).HasMaxLength(4000);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasOne(x => x.Master)
                .WithMany(x => x.Subordinates)
                .HasForeignKey(x => x.MasterId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ToolBinding>(e =>
        {
            e.ToTable("tool_bindings");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(128).IsRequired();
            e.Property(x => x.ConfigJson).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => new { x.AgentId, x.ToolName }).IsUnique();
            e.HasOne(x => x.Agent)
                .WithMany(x => x.ToolBindings)
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Plan>(e =>
        {
            e.ToTable("plans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Goal).HasMaxLength(4000).IsRequired();
            e.Property(x => x.MasterReviewStatus).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.MasterComment).HasMaxLength(4000);
            e.HasOne(x => x.Agent)
                .WithMany(x => x.Plans)
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.MasterReviewStatus);
        });

        modelBuilder.Entity<PlanStep>(e =>
        {
            e.ToTable("plan_steps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000).IsRequired();
            e.Property(x => x.ToolName).HasMaxLength(128);
            e.Property(x => x.ToolInputJson).HasColumnType("jsonb");
            e.Property(x => x.Justification).HasMaxLength(4000);
            e.Property(x => x.ResultSummary).HasMaxLength(8000);
            e.Property(x => x.DecisionClass).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.PlanId, x.Ordinal }).IsUnique();
            e.HasOne(x => x.Plan)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Checkpoint>(e =>
        {
            e.ToTable("checkpoints");
            e.HasKey(x => x.Id);
            e.Property(x => x.ContextJson).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => x.AgentId).IsUnique();
            e.HasOne(x => x.Agent)
                .WithMany(x => x.Checkpoints)
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ActionLog>(e =>
        {
            e.ToTable("action_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Category).HasMaxLength(64).IsRequired();
            e.Property(x => x.Message).HasMaxLength(4000).IsRequired();
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.AgentId, x.CreatedAt });
            e.HasOne(x => x.Agent)
                .WithMany(x => x.ActionLogs)
                .HasForeignKey(x => x.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
