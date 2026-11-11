using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatExposure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowToolSelection",
                schema: "agents",
                table: "assistants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Exposed",
                schema: "agents",
                table: "assistants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Installations behave as before the update: every existing assistant stays available to consumers (spec 018 D-19).
            migrationBuilder.Sql("UPDATE agents.assistants SET \"Exposed\" = TRUE;");

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                schema: "agents",
                table: "agent_tools",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Required");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowToolSelection",
                schema: "agents",
                table: "assistants");

            migrationBuilder.DropColumn(
                name: "Exposed",
                schema: "agents",
                table: "assistants");

            migrationBuilder.DropColumn(
                name: "Mode",
                schema: "agents",
                table: "agent_tools");
        }
    }
}
