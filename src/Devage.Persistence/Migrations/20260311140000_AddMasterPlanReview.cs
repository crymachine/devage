using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Devage.Persistence.Migrations;

[DbContext(typeof(DevageDbContext))]
[Migration("20260311140000_AddMasterPlanReview")]
public class AddMasterPlanReview : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "MasterComment",
            table: "plans",
            type: "character varying(4000)",
            maxLength: 4000,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "MasterDecidedAt",
            table: "plans",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MasterReviewStatus",
            table: "plans",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "NotRequired");

        migrationBuilder.Sql("""
            UPDATE plans
            SET "MasterReviewStatus" = CASE
                WHEN "MasterApproved" THEN 'Approved'
                ELSE 'NotRequired'
            END;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_plans_MasterReviewStatus",
            table: "plans",
            column: "MasterReviewStatus");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_plans_MasterReviewStatus",
            table: "plans");

        migrationBuilder.DropColumn(
            name: "MasterComment",
            table: "plans");

        migrationBuilder.DropColumn(
            name: "MasterDecidedAt",
            table: "plans");

        migrationBuilder.DropColumn(
            name: "MasterReviewStatus",
            table: "plans");
    }
}
