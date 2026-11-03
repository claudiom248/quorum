using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelEndpointName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_model_endpoints_TenantId",
                schema: "agents",
                table: "model_endpoints");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                schema: "agents",
                table: "model_endpoints",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // Existing endpoints are named after their model; the second and later ones of a tenant get a numeric suffix.
            migrationBuilder.Sql("""
                UPDATE agents.model_endpoints AS e
                SET "Name" = CASE WHEN r.rn = 1 THEN left(r."ModelName", 100) ELSE left(r."ModelName", 90) || '-' || r.rn END
                FROM (
                    SELECT "Id", "ModelName", row_number() OVER (PARTITION BY "TenantId", "ModelName" ORDER BY "UpdatedAt", "Id") AS rn
                    FROM agents.model_endpoints
                ) AS r
                WHERE e."Id" = r."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_model_endpoints_TenantId_Name",
                schema: "agents",
                table: "model_endpoints",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_model_endpoints_TenantId_Name",
                schema: "agents",
                table: "model_endpoints");

            migrationBuilder.DropColumn(
                name: "Name",
                schema: "agents",
                table: "model_endpoints");

            migrationBuilder.CreateIndex(
                name: "IX_model_endpoints_TenantId",
                schema: "agents",
                table: "model_endpoints",
                column: "TenantId");
        }
    }
}
