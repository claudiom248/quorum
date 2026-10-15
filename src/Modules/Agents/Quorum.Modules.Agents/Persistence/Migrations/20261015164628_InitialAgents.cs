using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "agents");

            migrationBuilder.CreateTable(
                name: "model_endpoints",
                schema: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ModelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "text", nullable: true),
                    ContextTokenBudget = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_endpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "assistants",
                schema: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    ModelEndpointId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assistants_model_endpoints_ModelEndpointId",
                        column: x => x.ModelEndpointId,
                        principalSchema: "agents",
                        principalTable: "model_endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assistants_ModelEndpointId",
                schema: "agents",
                table: "assistants",
                column: "ModelEndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_assistants_TenantId",
                schema: "agents",
                table: "assistants",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_model_endpoints_TenantId",
                schema: "agents",
                table: "model_endpoints",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistants",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "model_endpoints",
                schema: "agents");
        }
    }
}
