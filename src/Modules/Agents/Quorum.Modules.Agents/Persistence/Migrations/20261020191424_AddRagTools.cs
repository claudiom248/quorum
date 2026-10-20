using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRagTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tools_TenantId_Name",
                schema: "agents",
                table: "tools");

            migrationBuilder.AddColumn<int>(
                name: "SourceCount",
                schema: "agents",
                table: "tool_audit_entries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rag_tools",
                schema: "agents",
                columns: table => new
                {
                    ToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeBaseIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    ExposedFieldIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    FixedFilters = table.Column<string>(type: "jsonb", nullable: false),
                    FixedFilterFieldIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    TopK = table.Column<int>(type: "integer", nullable: false),
                    ScoreThreshold = table.Column<double>(type: "double precision", nullable: true),
                    MaxResultCharacters = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScopeTenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    ScopeAssistantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_tools", x => x.ToolId);
                    table.ForeignKey(
                        name: "FK_rag_tools_tools_ToolId",
                        column: x => x.ToolId,
                        principalSchema: "agents",
                        principalTable: "tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tools_TenantId_Origin_Name",
                schema: "agents",
                table: "tools",
                columns: new[] { "TenantId", "Origin", "Name" },
                unique: true,
                filter: "\"ServerId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_rag_tools_TenantId",
                schema: "agents",
                table: "rag_tools",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rag_tools",
                schema: "agents");

            migrationBuilder.DropIndex(
                name: "IX_tools_TenantId_Origin_Name",
                schema: "agents",
                table: "tools");

            migrationBuilder.DropColumn(
                name: "SourceCount",
                schema: "agents",
                table: "tool_audit_entries");

            migrationBuilder.CreateIndex(
                name: "IX_tools_TenantId_Name",
                schema: "agents",
                table: "tools",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "\"ServerId\" IS NULL");
        }
    }
}
