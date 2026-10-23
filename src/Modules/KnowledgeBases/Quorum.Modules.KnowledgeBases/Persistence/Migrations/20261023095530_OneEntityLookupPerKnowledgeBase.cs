using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.KnowledgeBases.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneEntityLookupPerKnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_lookups_one_entity_per_knowledge_base",
                schema: "knowledge_base",
                table: "lookups",
                column: "KnowledgeBaseId",
                unique: true,
                filter: "\"Kind\" = 'Entity'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lookups_one_entity_per_knowledge_base",
                schema: "knowledge_base",
                table: "lookups");
        }
    }
}
