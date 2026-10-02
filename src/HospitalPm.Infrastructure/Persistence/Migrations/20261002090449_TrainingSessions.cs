using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrainingSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "training_session",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    session_date = table.Column<DateOnly>(type: "date", nullable: false),
                    equipment_type_id = table.Column<int>(type: "integer", nullable: true),
                    trainer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    venue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    duration_minutes = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_session", x => x.id);
                    table.ForeignKey(
                        name: "FK_training_session_equipment_type_equipment_type_id",
                        column: x => x.equipment_type_id,
                        principalTable: "equipment_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_attendee",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    training_session_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    designation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_attendee", x => x.id);
                    table.ForeignKey(
                        name: "FK_training_attendee_training_session_training_session_id",
                        column: x => x.training_session_id,
                        principalTable: "training_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_training_attendee_session",
                table: "training_attendee",
                column: "training_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_attendee_user",
                table: "training_attendee",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_training_session_date",
                table: "training_session",
                column: "session_date");

            migrationBuilder.CreateIndex(
                name: "ix_training_session_equipment_type",
                table: "training_session",
                column: "equipment_type_id");

            // The rules the API checks, kept in the database too for anything that writes another way.
            migrationBuilder.Sql(@"
                ALTER TABLE training_session DROP CONSTRAINT IF EXISTS ck_training_session_title;
                ALTER TABLE training_session
                    ADD CONSTRAINT ck_training_session_title CHECK (length(btrim(title)) > 0);

                ALTER TABLE training_session DROP CONSTRAINT IF EXISTS ck_training_session_duration;
                ALTER TABLE training_session
                    ADD CONSTRAINT ck_training_session_duration
                    CHECK (duration_minutes IS NULL OR duration_minutes BETWEEN 1 AND 1440);

                ALTER TABLE training_attendee DROP CONSTRAINT IF EXISTS ck_training_attendee_name;
                ALTER TABLE training_attendee
                    ADD CONSTRAINT ck_training_attendee_name CHECK (length(btrim(name)) > 0);");

            // Audit is written by Postgres, never by application code. Who was trained on what is a
            // record the department can be asked to account for, the same reasoning as spare_part.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_training_session_audit ON training_session;
                CREATE TRIGGER trg_training_session_audit
                    AFTER INSERT OR UPDATE OR DELETE ON training_session
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();

                DROP TRIGGER IF EXISTS trg_training_attendee_audit ON training_attendee;
                CREATE TRIGGER trg_training_attendee_audit
                    AFTER INSERT OR UPDATE OR DELETE ON training_attendee
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "training_attendee");

            migrationBuilder.DropTable(
                name: "training_session");
        }
    }
}
