using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRagToolDocumentIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowDocumentIds",
                schema: "agents",
                table: "rag_tools",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowDocumentIds",
                schema: "agents",
                table: "rag_tools");
        }
    }
}
