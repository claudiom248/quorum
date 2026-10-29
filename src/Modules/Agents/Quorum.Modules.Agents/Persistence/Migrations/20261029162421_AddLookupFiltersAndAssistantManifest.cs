using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLookupFiltersAndAssistantManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LookupFilters",
                schema: "agents",
                table: "lookup_tools",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Manifest",
                schema: "agents",
                table: "assistant_tools",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Full");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LookupFilters",
                schema: "agents",
                table: "lookup_tools");

            migrationBuilder.DropColumn(
                name: "Manifest",
                schema: "agents",
                table: "assistant_tools");
        }
    }
}
