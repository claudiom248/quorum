using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Tenancy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLedgerStyle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LedgerStyle",
                schema: "tenancy",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LedgerStyle",
                schema: "tenancy",
                table: "users");
        }
    }
}
