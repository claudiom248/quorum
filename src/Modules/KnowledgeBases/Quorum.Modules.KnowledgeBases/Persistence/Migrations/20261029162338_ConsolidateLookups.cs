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
            // Existing rows get their names and descriptions before the columns become required (spec 012 D-2, D-10).
            migrationBuilder.Sql("""
                UPDATE knowledge_base.lookups AS l
                SET "Name" = kb."Name" || '.' || f."Name",
                    "Description" = 'Distinct values of ' || f."Name" || ' in ' || kb."Name"
                FROM knowledge_base.knowledge_bases AS kb, knowledge_base.dictionary_fields AS f
                WHERE l."KnowledgeBaseId" = kb."Id" AND l."DictionaryFieldId" = f."Id" AND l."Kind" = 'Computed';
                """);
            migrationBuilder.Sql("""
                UPDATE knowledge_base.lookups AS l
                SET "Name" = kb."Name",
                    "Description" = 'Entities of ' || kb."Name"
                FROM knowledge_base.knowledge_bases AS kb
                WHERE l."KnowledgeBaseId" = kb."Id" AND l."Kind" = 'Entity';
                """);
            migrationBuilder.Sql("""
                UPDATE knowledge_base.lookups SET "Description" = 'A list of values.' WHERE "Description" IS NULL;
                """);

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

            // Every existing lookup is a snapshot, and an owned collection reads a null column as a mistake: it starts empty.
            migrationBuilder.Sql("""UPDATE knowledge_base.lookups SET "Storage" = 'Snapshot' WHERE "Storage" = '';""");
            migrationBuilder.Sql("""UPDATE knowledge_base.lookups SET "Attributes" = '[]'::jsonb WHERE "Attributes" IS NULL;""");
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
