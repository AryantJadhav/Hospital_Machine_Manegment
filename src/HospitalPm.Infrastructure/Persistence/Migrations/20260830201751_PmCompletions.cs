using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PmCompletions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pm_completion",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    pm_task_id = table.Column<int>(type: "integer", nullable: false),
                    checklist_template_version_id = table.Column<int>(type: "integer", nullable: false),
                    answers = table.Column<string>(type: "jsonb", nullable: false),
                    signature_png = table.Column<byte[]>(type: "bytea", nullable: true),
                    signed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    completed_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    performed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    client_submission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pm_completion", x => x.id);
                    table.ForeignKey(
                        name: "FK_pm_completion_pm_task_pm_task_id",
                        column: x => x.pm_task_id,
                        principalTable: "pm_task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_pm_completion_client_submission",
                table: "pm_completion",
                column: "client_submission_id",
                unique: true,
                filter: "client_submission_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_pm_completion_task",
                table: "pm_completion",
                column: "pm_task_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pm_completion");
        }
    }
}
