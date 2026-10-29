using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateWebConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.DropTable(
                name: "web_search_configuration",
                schema: "agents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
