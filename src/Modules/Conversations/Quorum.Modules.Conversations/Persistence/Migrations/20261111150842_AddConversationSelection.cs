using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Conversations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "DisabledTools",
                schema: "conversations",
                table: "conversations",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                schema: "conversations",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Consumer");

            migrationBuilder.AddColumn<Guid>(
                name: "OverrideModelEndpointId",
                schema: "conversations",
                table: "conversations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisabledTools",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Mode",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "OverrideModelEndpointId",
                schema: "conversations",
                table: "conversations");
        }
    }
}
