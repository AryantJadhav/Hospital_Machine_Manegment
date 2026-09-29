using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_order_attachment",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    work_order_id = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    uploaded_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    uploaded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_attachment", x => x.id);
                    table.ForeignKey(
                        name: "FK_work_order_attachment_work_order_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "work_order_attachment_data",
                columns: table => new
                {
                    attachment_id = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_attachment_data", x => x.attachment_id);
                    table.ForeignKey(
                        name: "FK_work_order_attachment_data_work_order_attachment_attachment~",
                        column: x => x.attachment_id,
                        principalTable: "work_order_attachment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_order_attachment_order",
                table: "work_order_attachment",
                column: "work_order_id");

            migrationBuilder.Sql(@"
                ALTER TABLE work_order_attachment ADD CONSTRAINT ck_work_order_attachment_size CHECK (size_bytes > 0);

                -- A file is never edited. A wrong one is removed by an Administrator and uploaded again.
                CREATE OR REPLACE FUNCTION fn_work_order_attachment_no_update() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION 'Attachment % is a record and cannot be changed; remove it and upload again', OLD.id
                        USING ERRCODE = 'check_violation';
                END;
                $fn$;

                DROP TRIGGER IF EXISTS trg_work_order_attachment_no_update ON work_order_attachment;
                CREATE TRIGGER trg_work_order_attachment_no_update
                    BEFORE UPDATE ON work_order_attachment
                    FOR EACH ROW EXECUTE FUNCTION fn_work_order_attachment_no_update();

                -- Audit is written by Postgres, never by application code. Only the description is
                -- audited, not work_order_attachment_data, so the audit log copies a few lines of
                -- text here and not a whole photo a second time.
                DROP TRIGGER IF EXISTS trg_work_order_attachment_audit ON work_order_attachment;
                CREATE TRIGGER trg_work_order_attachment_audit
                    AFTER INSERT OR UPDATE OR DELETE ON work_order_attachment
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order_attachment_data");

            migrationBuilder.DropTable(
                name: "work_order_attachment");
        }
    }
}
