using Devage.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace Devage.Persistence.Migrations;

[DbContext(typeof(DevageDbContext))]
[Migration("20260311120000_InitialCreate")]
partial class InitialCreate
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "8.0.14")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("Devage.Core.Domain.ActionLog", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("AgentId").HasColumnType("uuid");
            b.Property<string>("Category").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("DetailsJson").HasColumnType("jsonb");
            b.Property<string>("Message").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<Guid?>("PlanId").HasColumnType("uuid");
            b.Property<Guid?>("StepId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("AgentId", "CreatedAt");
            b.ToTable("action_logs", (string)null);
        });

        modelBuilder.Entity("Devage.Core.Domain.Agent", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Goal").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<Guid?>("MasterId").HasColumnType("uuid");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("Role").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("Status").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("WorkspaceRoot").IsRequired().HasMaxLength(1024).HasColumnType("character varying(1024)");
            b.HasKey("Id");
            b.HasIndex("MasterId");
            b.HasIndex("Name").IsUnique();
            b.ToTable("agents", (string)null);
        });

        modelBuilder.Entity("Devage.Core.Domain.Checkpoint", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("AgentId").HasColumnType("uuid");
            b.Property<string>("ContextJson").IsRequired().HasColumnType("jsonb");
            b.Property<Guid?>("CurrentStepId").HasColumnType("uuid");
            b.Property<Guid?>("PlanId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("AgentId").IsUnique();
            b.ToTable("checkpoints", (string)null);
        });

        modelBuilder.Entity("Devage.Core.Domain.Plan", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("AgentId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Goal").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<bool>("MasterApproved").HasColumnType("boolean");
            b.Property<string>("Title").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("AgentId");
            b.ToTable("plans", (string)null);
        });

        modelBuilder.Entity("Devage.Core.Domain.PlanStep", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("DecisionClass").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("Description").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Justification").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<int>("Ordinal").HasColumnType("integer");
            b.Property<Guid>("PlanId").HasColumnType("uuid");
            b.Property<string>("ResultSummary").HasMaxLength(8000).HasColumnType("character varying(8000)");
            b.Property<DateTimeOffset?>("StartedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Status").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("Title").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("ToolInputJson").HasColumnType("jsonb");
            b.Property<string>("ToolName").HasMaxLength(128).HasColumnType("character varying(128)");
            b.HasKey("Id");
            b.HasIndex("PlanId", "Ordinal").IsUnique();
            b.ToTable("plan_steps", (string)null);
        });

        modelBuilder.Entity("Devage.Core.Domain.ToolBinding", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("AgentId").HasColumnType("uuid");
            b.Property<string>("ConfigJson").IsRequired().HasColumnType("jsonb");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<string>("ToolName").IsRequired().HasMaxLength(128).HasColumnType("character varying(128)");
            b.HasKey("Id");
            b.HasIndex("AgentId", "ToolName").IsUnique();
            b.ToTable("tool_bindings", (string)null);
        });

        modelBuilder.Entity("Devage.Core.Domain.ActionLog", b =>
        {
            b.HasOne("Devage.Core.Domain.Agent", "Agent")
                .WithMany("ActionLogs")
                .HasForeignKey("AgentId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Agent");
        });

        modelBuilder.Entity("Devage.Core.Domain.Agent", b =>
        {
            b.HasOne("Devage.Core.Domain.Agent", "Master")
                .WithMany("Subordinates")
                .HasForeignKey("MasterId")
                .OnDelete(DeleteBehavior.SetNull);
            b.Navigation("Master");
        });

        modelBuilder.Entity("Devage.Core.Domain.Checkpoint", b =>
        {
            b.HasOne("Devage.Core.Domain.Agent", "Agent")
                .WithMany("Checkpoints")
                .HasForeignKey("AgentId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Agent");
        });

        modelBuilder.Entity("Devage.Core.Domain.Plan", b =>
        {
            b.HasOne("Devage.Core.Domain.Agent", "Agent")
                .WithMany("Plans")
                .HasForeignKey("AgentId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Agent");
        });

        modelBuilder.Entity("Devage.Core.Domain.PlanStep", b =>
        {
            b.HasOne("Devage.Core.Domain.Plan", "Plan")
                .WithMany("Steps")
                .HasForeignKey("PlanId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Plan");
        });

        modelBuilder.Entity("Devage.Core.Domain.ToolBinding", b =>
        {
            b.HasOne("Devage.Core.Domain.Agent", "Agent")
                .WithMany("ToolBindings")
                .HasForeignKey("AgentId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Agent");
        });

        modelBuilder.Entity("Devage.Core.Domain.Agent", b =>
        {
            b.Navigation("ActionLogs");
            b.Navigation("Checkpoints");
            b.Navigation("Plans");
            b.Navigation("Subordinates");
            b.Navigation("ToolBindings");
        });

        modelBuilder.Entity("Devage.Core.Domain.Plan", b =>
        {
            b.Navigation("Steps");
        });
#pragma warning restore 612, 618
    }
}
