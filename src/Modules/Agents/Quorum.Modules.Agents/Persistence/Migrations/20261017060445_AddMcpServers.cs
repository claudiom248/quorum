using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tools_TenantId_Origin_Name",
                schema: "agents",
                table: "tools");

            migrationBuilder.AddColumn<string>(
                name: "Annotations",
                schema: "agents",
                table: "tools",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefinitionHash",
                schema: "agents",
                table: "tools",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServerId",
                schema: "agents",
                table: "tools",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mcp_servers",
                schema: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ProtectedHeaders = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastRefreshedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScopeTenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    ScopeAssistantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_servers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tools_ServerId_Name",
                schema: "agents",
                table: "tools",
                columns: new[] { "ServerId", "Name" },
                unique: true,
                filter: "\"ServerId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tools_TenantId_Name",
                schema: "agents",
                table: "tools",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "\"ServerId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_TenantId_Name",
                schema: "agents",
                table: "mcp_servers",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_tools_mcp_servers_ServerId",
                schema: "agents",
                table: "tools",
                column: "ServerId",
                principalSchema: "agents",
                principalTable: "mcp_servers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // MCP tools cannot exist without their server; dropping them lets the old per-tenant unique index come back.
            migrationBuilder.Sql("DELETE FROM agents.tools WHERE \"Origin\" = 'Mcp'");

            migrationBuilder.DropForeignKey(
                name: "FK_tools_mcp_servers_ServerId",
                schema: "agents",
                table: "tools");

            migrationBuilder.DropTable(
                name: "mcp_servers",
                schema: "agents");

            migrationBuilder.DropIndex(
                name: "IX_tools_ServerId_Name",
                schema: "agents",
                table: "tools");

            migrationBuilder.DropIndex(
                name: "IX_tools_TenantId_Name",
                schema: "agents",
                table: "tools");

            migrationBuilder.DropColumn(
                name: "Annotations",
                schema: "agents",
                table: "tools");

            migrationBuilder.DropColumn(
                name: "DefinitionHash",
                schema: "agents",
                table: "tools");

            migrationBuilder.DropColumn(
                name: "ServerId",
                schema: "agents",
                table: "tools");

            migrationBuilder.CreateIndex(
                name: "IX_tools_TenantId_Origin_Name",
                schema: "agents",
                table: "tools",
                columns: new[] { "TenantId", "Origin", "Name" },
                unique: true);
        }
    }
}
