using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PermissionGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_permission",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    permission = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    effect = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    granted_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    granted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_permission", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_permission_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_user_permission_user_permission",
                table: "user_permission",
                columns: new[] { "user_id", "permission" },
                unique: true);

            // The rules the API checks, kept in the database too for anything that writes another way.
            migrationBuilder.Sql(@"
                ALTER TABLE user_permission DROP CONSTRAINT IF EXISTS ck_user_permission_effect;
                ALTER TABLE user_permission
                    ADD CONSTRAINT ck_user_permission_effect CHECK (effect IN ('Grant', 'Revoke'));");

            // Audit is written by Postgres, never by application code. Who was given access to what,
            // and by whom, is exactly the record a hospital is entitled to ask for.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_user_permission_audit ON user_permission;
                CREATE TRIGGER trg_user_permission_audit
                    AFTER INSERT OR UPDATE OR DELETE ON user_permission
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_permission");
        }
    }
}
