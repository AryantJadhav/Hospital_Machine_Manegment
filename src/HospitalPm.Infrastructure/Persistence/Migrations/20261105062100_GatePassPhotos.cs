using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GatePassPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gate_pass_attachment",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    gate_pass_id = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    uploaded_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    uploaded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_pass_attachment", x => x.id);
                    table.ForeignKey(
                        name: "FK_gate_pass_attachment_gate_pass_gate_pass_id",
                        column: x => x.gate_pass_id,
                        principalTable: "gate_pass",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gate_pass_attachment_data",
                columns: table => new
                {
                    attachment_id = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_pass_attachment_data", x => x.attachment_id);
                    table.ForeignKey(
                        name: "FK_gate_pass_attachment_data_gate_pass_attachment_attachment_id",
                        column: x => x.attachment_id,
                        principalTable: "gate_pass_attachment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gate_pass_attachment_pass",
                table: "gate_pass_attachment",
                column: "gate_pass_id");

            migrationBuilder.Sql(@"
                ALTER TABLE gate_pass_attachment DROP CONSTRAINT IF EXISTS ck_gate_pass_attachment_size;
                ALTER TABLE gate_pass_attachment ADD CONSTRAINT ck_gate_pass_attachment_size CHECK (size_bytes > 0);

                -- A photo is never edited. A wrong one is removed by an Administrator and uploaded again.
                CREATE OR REPLACE FUNCTION fn_gate_pass_attachment_no_update() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION 'Photo % is a record and cannot be changed; remove it and upload again', OLD.id
                        USING ERRCODE = 'check_violation';
                END;
                $fn$;

                DROP TRIGGER IF EXISTS trg_gate_pass_attachment_no_update ON gate_pass_attachment;
                CREATE TRIGGER trg_gate_pass_attachment_no_update
                    BEFORE UPDATE ON gate_pass_attachment
                    FOR EACH ROW EXECUTE FUNCTION fn_gate_pass_attachment_no_update();

                -- Audit is written by Postgres, never by application code. Only the description is audited, not the
                -- bytes, so the audit log does not copy a whole photo a second time.
                DROP TRIGGER IF EXISTS trg_gate_pass_attachment_audit ON gate_pass_attachment;
                CREATE TRIGGER trg_gate_pass_attachment_audit
                    AFTER INSERT OR UPDATE OR DELETE ON gate_pass_attachment
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gate_pass_attachment_data");

            migrationBuilder.DropTable(
                name: "gate_pass_attachment");
        }
    }
}
