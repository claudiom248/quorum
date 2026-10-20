using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_sources",
                schema: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    N = table.Column<int>(type: "integer", nullable: false),
                    KnowledgeBase = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DocumentId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Position = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_message_sources_messages_MessageId",
                        column: x => x.MessageId,
                        principalSchema: "conversations",
                        principalTable: "messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_message_sources_MessageId_N",
                schema: "conversations",
                table: "message_sources",
                columns: new[] { "MessageId", "N" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_sources",
                schema: "conversations");
        }
    }
}
