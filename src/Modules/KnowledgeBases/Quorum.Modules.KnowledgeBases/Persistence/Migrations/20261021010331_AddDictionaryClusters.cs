using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.KnowledgeBases.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDictionaryClusters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dictionary_fields_TenantId_Name",
                schema: "knowledge_base",
                table: "dictionary_fields");

            migrationBuilder.AddColumn<string>(
                name: "Cluster",
                schema: "knowledge_base",
                table: "dictionary_fields",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_dictionary_fields_TenantId_Cluster_Name",
                schema: "knowledge_base",
                table: "dictionary_fields",
                columns: new[] { "TenantId", "Cluster", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dictionary_fields_TenantId_Cluster_Name",
                schema: "knowledge_base",
                table: "dictionary_fields");

            migrationBuilder.DropColumn(
                name: "Cluster",
                schema: "knowledge_base",
                table: "dictionary_fields");

            migrationBuilder.CreateIndex(
                name: "IX_dictionary_fields_TenantId_Name",
                schema: "knowledge_base",
                table: "dictionary_fields",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }
    }
}
