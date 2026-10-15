using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attachments",
                schema: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MediaType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    CharacterCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_attachments_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "conversations",
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attachments_ConversationId_ContentHash",
                schema: "conversations",
                table: "attachments",
                columns: new[] { "ConversationId", "ContentHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments",
                schema: "conversations");
        }
    }
}
