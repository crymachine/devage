using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Devage.Persistence.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "agents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                MasterId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                WorkspaceRoot = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                Goal = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agents", x => x.Id);
                table.ForeignKey(
                    name: "FK_agents_agents_MasterId",
                    column: x => x.MasterId,
                    principalTable: "agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "action_logs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                StepId = table.Column<Guid>(type: "uuid", nullable: true),
                Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                DetailsJson = table.Column<string>(type: "jsonb", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_action_logs", x => x.Id);
                table.ForeignKey(
                    name: "FK_action_logs_agents_AgentId",
                    column: x => x.AgentId,
                    principalTable: "agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "checkpoints",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                CurrentStepId = table.Column<Guid>(type: "uuid", nullable: true),
                ContextJson = table.Column<string>(type: "jsonb", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_checkpoints", x => x.Id);
                table.ForeignKey(
                    name: "FK_checkpoints_agents_AgentId",
                    column: x => x.AgentId,
                    principalTable: "agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "plans",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Goal = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                MasterApproved = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_plans", x => x.Id);
                table.ForeignKey(
                    name: "FK_plans_agents_AgentId",
                    column: x => x.AgentId,
                    principalTable: "agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "tool_bindings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ConfigJson = table.Column<string>(type: "jsonb", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tool_bindings", x => x.Id);
                table.ForeignKey(
                    name: "FK_tool_bindings_agents_AgentId",
                    column: x => x.AgentId,
                    principalTable: "agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "plan_steps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                Ordinal = table.Column<int>(type: "integer", nullable: false),
                Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ToolInputJson = table.Column<string>(type: "jsonb", nullable: true),
                DecisionClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Justification = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                ResultSummary = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_plan_steps", x => x.Id);
                table.ForeignKey(
                    name: "FK_plan_steps_plans_PlanId",
                    column: x => x.PlanId,
                    principalTable: "plans",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_action_logs_AgentId_CreatedAt", table: "action_logs", columns: new[] { "AgentId", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_agents_MasterId", table: "agents", column: "MasterId");
        migrationBuilder.CreateIndex(name: "IX_agents_Name", table: "agents", column: "Name", unique: true);
        migrationBuilder.CreateIndex(name: "IX_checkpoints_AgentId", table: "checkpoints", column: "AgentId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_plan_steps_PlanId_Ordinal", table: "plan_steps", columns: new[] { "PlanId", "Ordinal" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_plans_AgentId", table: "plans", column: "AgentId");
        migrationBuilder.CreateIndex(name: "IX_tool_bindings_AgentId_ToolName", table: "tool_bindings", columns: new[] { "AgentId", "ToolName" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "action_logs");
        migrationBuilder.DropTable(name: "checkpoints");
        migrationBuilder.DropTable(name: "plan_steps");
        migrationBuilder.DropTable(name: "tool_bindings");
        migrationBuilder.DropTable(name: "plans");
        migrationBuilder.DropTable(name: "agents");
    }
}
