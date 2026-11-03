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

            // Existing endpoints are named after their model; the second and later ones of a tenant get the endpoint id as suffix,
            // which cannot collide with another model name (a numeric suffix could, and so could names cut to the length limit).
            migrationBuilder.Sql("""
                UPDATE agents.model_endpoints AS e
                SET "Name" = CASE WHEN r.rn = 1 THEN left(r."ModelName", 100) ELSE left(r."ModelName", 60) || '-' || replace(e."Id"::text, '-', '') END
                FROM (
                    SELECT "Id", "ModelName", row_number() OVER (PARTITION BY "TenantId", left("ModelName", 100) ORDER BY "UpdatedAt", "Id") AS rn
                    FROM agents.model_endpoints
                ) AS r
                WHERE e."Id" = r."Id";
                """);

            // Deleting an endpoint must never cascade to the assistants that use it.
            migrationBuilder.DropForeignKey(
                name: "FK_assistants_model_endpoints_ModelEndpointId",
                schema: "agents",
                table: "assistants");

            migrationBuilder.AddForeignKey(
                name: "FK_assistants_model_endpoints_ModelEndpointId",
                schema: "agents",
                table: "assistants",
                column: "ModelEndpointId",
                principalSchema: "agents",
                principalTable: "model_endpoints",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

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
            migrationBuilder.DropForeignKey(
                name: "FK_assistants_model_endpoints_ModelEndpointId",
                schema: "agents",
                table: "assistants");

            migrationBuilder.AddForeignKey(
                name: "FK_assistants_model_endpoints_ModelEndpointId",
                schema: "agents",
                table: "assistants",
                column: "ModelEndpointId",
                principalSchema: "agents",
                principalTable: "model_endpoints",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

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
