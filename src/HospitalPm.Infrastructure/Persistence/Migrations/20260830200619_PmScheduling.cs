using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PmScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pm_schedule",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    equipment_id = table.Column<int>(type: "integer", nullable: false),
                    checklist_template_id = table.Column<int>(type: "integer", nullable: false),
                    frequency = table.Column<int>(type: "integer", nullable: false),
                    interval_days = table.Column<int>(type: "integer", nullable: false),
                    anchor_date = table.Column<DateOnly>(type: "date", nullable: false),
                    grace_days = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pm_schedule", x => x.id);
                    table.ForeignKey(
                        name: "FK_pm_schedule_checklist_template_checklist_template_id",
                        column: x => x.checklist_template_id,
                        principalTable: "checklist_template",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pm_schedule_equipment_equipment_id",
                        column: x => x.equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pm_task",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    pm_schedule_id = table.Column<int>(type: "integer", nullable: false),
                    equipment_id = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    checklist_template_version_id = table.Column<int>(type: "integer", nullable: true),
                    skip_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pm_task", x => x.id);
                    table.ForeignKey(
                        name: "FK_pm_task_equipment_equipment_id",
                        column: x => x.equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pm_task_pm_schedule_pm_schedule_id",
                        column: x => x.pm_schedule_id,
                        principalTable: "pm_schedule",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pm_schedule_checklist_template_id",
                table: "pm_schedule",
                column: "checklist_template_id");

            migrationBuilder.CreateIndex(
                name: "ux_pm_schedule_equipment_checklist",
                table: "pm_schedule",
                columns: new[] { "equipment_id", "checklist_template_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pm_task_equipment",
                table: "pm_task",
                column: "equipment_id");

            migrationBuilder.CreateIndex(
                name: "ix_pm_task_status_due",
                table: "pm_task",
                columns: new[] { "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ux_pm_task_schedule_due",
                table: "pm_task",
                columns: new[] { "pm_schedule_id", "due_date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pm_task");

            migrationBuilder.DropTable(
                name: "pm_schedule");
        }
    }
}
