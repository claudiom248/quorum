using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.KnowledgeBases.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateLookups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows get their names, descriptions and attributes before the columns become required (spec 012 D-2, D-3, D-10).
            migrationBuilder.Sql(ConsolidateLookupsBackfill.ComputedNames);
            migrationBuilder.Sql(ConsolidateLookupsBackfill.EntityNames);
            migrationBuilder.Sql(ConsolidateLookupsBackfill.Descriptions);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                schema: "knowledge_base",
                table: "lookups",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                schema: "knowledge_base",
                table: "lookups",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Attributes",
                schema: "knowledge_base",
                table: "lookups",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Storage",
                schema: "knowledge_base",
                table: "lookups",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(ConsolidateLookupsBackfill.Storage);
            migrationBuilder.Sql(ConsolidateLookupsBackfill.EntityAttributes);
            migrationBuilder.Sql(ConsolidateLookupsBackfill.EmptyAttributes);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attributes",
                schema: "knowledge_base",
                table: "lookups");

            migrationBuilder.DropColumn(
                name: "Storage",
                schema: "knowledge_base",
                table: "lookups");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                schema: "knowledge_base",
                table: "lookups",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                schema: "knowledge_base",
                table: "lookups",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);
        }
    }
}
