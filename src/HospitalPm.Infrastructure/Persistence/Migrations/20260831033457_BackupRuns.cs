using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations;

/// <summary>
/// The record of what has been backed up.
///
/// Failed runs are rows too. A hospital that assumes it has backups because
/// nothing complained is in a worse position than one that knows it has
/// none, so the dashboard reads the latest run whatever its outcome and the
/// reason for a failure is kept in words an administrator can act on.
///
/// No audit trigger on this table. Audit exists to record who changed the
/// equipment record; a backup log is machine-written operational history,
/// and every row here is already an immutable statement of one event.
/// </summary>
public partial class BackupRuns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "backup_run",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                trigger = table.Column<int>(type: "integer", nullable: false),
                file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                size_bytes = table.Column<long>(type: "bigint", nullable: true),
                error = table.Column<string>(type: "text", nullable: true),
                server_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                duration_ms = table.Column<int>(type: "integer", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_backup_run", x => x.id);
            });

        // The dashboard asks one question — what happened most recently — on
        // every page load.
        migrationBuilder.CreateIndex(
            name: "ix_backup_run_recent",
            table: "backup_run",
            column: "started_at_utc",
            descending: new bool[0]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException(
            "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
}
