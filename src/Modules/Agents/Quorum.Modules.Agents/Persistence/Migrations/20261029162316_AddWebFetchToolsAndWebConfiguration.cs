using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebFetchToolsAndWebConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The platform's one web row (specs 005 and 006) replaces the SearXNG-only one: the old table is dropped last, so
            // the INSERT below can still read what an administrator had saved.
            migrationBuilder.CreateTable(
                name: "web_configuration",
                schema: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SearchUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SearchTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    FetchTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    FetchMaxUrlsPerInvocation = table.Column<int>(type: "integer", nullable: false),
                    FetchTokenBudgetPerPage = table.Column<int>(type: "integer", nullable: false),
                    FetchTotalTokenBudget = table.Column<int>(type: "integer", nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FetchDelayMs = table.Column<int>(type: "integer", nullable: false),
                    RespectRobotsTxt = table.Column<bool>(type: "boolean", nullable: false),
                    CacheEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CacheTtlMinutes = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_web_configuration", x => x.Id);
                });

            // Carry over the SearXNG row if an administrator already saved one; the rest starts from the platform's defaults.
            // The numbers are the defaults of WebConfiguration, written here so a fresh installation reads what the code says.
            migrationBuilder.Sql("""
                INSERT INTO agents.web_configuration (
                    "Id", "SearchUrl", "SearchTimeoutSeconds", "FetchTimeoutSeconds",
                    "FetchMaxUrlsPerInvocation", "FetchTokenBudgetPerPage", "FetchTotalTokenBudget",
                    "UserAgent", "FetchDelayMs", "RespectRobotsTxt", "CacheEnabled", "CacheTtlMinutes", "UpdatedAt")
                VALUES (
                    '00000000-0000-0000-0000-000000000001',
                    COALESCE((SELECT "Url" FROM agents.web_search_configuration WHERE "Id" = '00000000-0000-0000-0000-000000000001'), NULL),
                    COALESCE((SELECT "TimeoutSeconds" FROM agents.web_search_configuration WHERE "Id" = '00000000-0000-0000-0000-000000000001'), 90),
                    30, 5, 4000, 20000,
                    'Quorum/1.0', 1000, TRUE, TRUE, 10,
                    COALESCE((SELECT "UpdatedAt" FROM agents.web_search_configuration WHERE "Id" = '00000000-0000-0000-0000-000000000001'), NOW() AT TIME ZONE 'UTC'))
                ON CONFLICT ("Id") DO NOTHING;
                """);

            // A web fetch tool keeps only what the administrator set apart from the platform: a null is not a number but the
            // absence of one, so the tool follows the row at the time of the call (spec 006, AC-1 and AC-13).
            migrationBuilder.CreateTable(
                name: "web_fetch_tools",
                schema: "agents",
                columns: table => new
                {
                    ToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenBudgetPerPage = table.Column<int>(type: "integer", nullable: true),
                    TotalTokenBudget = table.Column<int>(type: "integer", nullable: true),
                    MaxUrlsPerInvocation = table.Column<int>(type: "integer", nullable: true),
                    Timeout = table.Column<int>(type: "integer", nullable: true),
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

            migrationBuilder.DropTable(
                name: "web_search_configuration",
                schema: "agents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "web_fetch_tools",
                schema: "agents");

            migrationBuilder.CreateTable(
                name: "web_search_configuration",
                schema: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_web_search_configuration", x => x.Id);
                });

            // Restore the SearXNG row from the unified one, if the administrator had set it.
            migrationBuilder.Sql("""
                INSERT INTO agents.web_search_configuration ("Id", "Url", "TimeoutSeconds", "UpdatedAt")
                SELECT "Id", "SearchUrl", "SearchTimeoutSeconds", "UpdatedAt"
                FROM agents.web_configuration
                WHERE "Id" = '00000000-0000-0000-0000-000000000001' AND "SearchUrl" IS NOT NULL
                ON CONFLICT ("Id") DO NOTHING;
                """);

            migrationBuilder.DropTable(
                name: "web_configuration",
                schema: "agents");
        }
    }
}
