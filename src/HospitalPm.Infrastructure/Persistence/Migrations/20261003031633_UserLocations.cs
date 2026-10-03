using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_location",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    location_id = table.Column<int>(type: "integer", nullable: false),
                    assigned_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    assigned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_location", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_location_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_location_location_location_id",
                        column: x => x.location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_location_location",
                table: "user_location",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_location_user_location",
                table: "user_location",
                columns: new[] { "user_id", "location_id" },
                unique: true);

            // Audit is written by Postgres, never by application code. Which departments a person was
            // given, and by whom, decides what they can see, so it is a record worth keeping.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_user_location_audit ON user_location;
                CREATE TRIGGER trg_user_location_audit
                    AFTER INSERT OR UPDATE OR DELETE ON user_location
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_location");
        }
    }
}
