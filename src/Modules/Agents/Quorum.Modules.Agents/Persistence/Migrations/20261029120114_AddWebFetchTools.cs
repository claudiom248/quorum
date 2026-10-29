using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebFetchTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "web_fetch_tools",
                schema: "agents",
                columns: table => new
                {
                    ToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenBudgetPerPage = table.Column<int>(type: "integer", nullable: false),
                    TotalTokenBudget = table.Column<int>(type: "integer", nullable: false),
                    MaxUrlsPerInvocation = table.Column<int>(type: "integer", nullable: false),
                    Timeout = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScopeTenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    ScopeAssistantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_web_fetch_tools", x => x.ToolId);
                    table.ForeignKey(
                        name: "FK_web_fetch_tools_tools_ToolId",
                        column: x => x.ToolId,
                        principalSchema: "agents",
                        principalTable: "tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_web_fetch_tools_TenantId",
                schema: "agents",
                table: "web_fetch_tools",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "web_fetch_tools",
                schema: "agents");
        }
    }
}
