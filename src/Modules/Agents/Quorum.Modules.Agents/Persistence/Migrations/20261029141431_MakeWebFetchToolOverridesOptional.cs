using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeWebFetchToolOverridesOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "TotalTokenBudget",
                schema: "agents",
                table: "web_fetch_tools",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "TokenBudgetPerPage",
                schema: "agents",
                table: "web_fetch_tools",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "Timeout",
                schema: "agents",
                table: "web_fetch_tools",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "MaxUrlsPerInvocation",
                schema: "agents",
                table: "web_fetch_tools",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // Every tool created until now was handed the numbers the API resolved for it. A field that still says what the
            // platform's row says was never an override: it goes back to inheriting, so the row reaches it from now on
            // (spec 006, AC-13). A field the administrator set apart from the default stays a real override.
            migrationBuilder.Sql("""
                UPDATE agents.web_fetch_tools b
                   SET "TokenBudgetPerPage" = NULL
                  FROM agents.web_configuration w
                 WHERE b."TokenBudgetPerPage" = w."FetchTokenBudgetPerPage";
                UPDATE agents.web_fetch_tools b
                   SET "TotalTokenBudget" = NULL
                  FROM agents.web_configuration w
                 WHERE b."TotalTokenBudget" = w."FetchTotalTokenBudget";
                UPDATE agents.web_fetch_tools b
                   SET "MaxUrlsPerInvocation" = NULL
                  FROM agents.web_configuration w
                 WHERE b."MaxUrlsPerInvocation" = w."FetchMaxUrlsPerInvocation";
                UPDATE agents.web_fetch_tools b
                   SET "Timeout" = NULL
                  FROM agents.web_configuration w
                 WHERE b."Timeout" = w."FetchTimeoutSeconds";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The columns go back to holding a number each: an inheriting tool takes the platform's, which is what it used.
            migrationBuilder.Sql("""
                UPDATE agents.web_fetch_tools b
                   SET "TokenBudgetPerPage" = w."FetchTokenBudgetPerPage",
                       "TotalTokenBudget" = w."FetchTotalTokenBudget",
                       "MaxUrlsPerInvocation" = w."FetchMaxUrlsPerInvocation",
                       "Timeout" = w."FetchTimeoutSeconds"
                  FROM agents.web_configuration w
                 WHERE b."TokenBudgetPerPage" IS NULL
                    OR b."TotalTokenBudget" IS NULL
                    OR b."MaxUrlsPerInvocation" IS NULL
                    OR b."Timeout" IS NULL;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "TotalTokenBudget",
                schema: "agents",
                table: "web_fetch_tools",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "TokenBudgetPerPage",
                schema: "agents",
                table: "web_fetch_tools",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Timeout",
                schema: "agents",
                table: "web_fetch_tools",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MaxUrlsPerInvocation",
                schema: "agents",
                table: "web_fetch_tools",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
