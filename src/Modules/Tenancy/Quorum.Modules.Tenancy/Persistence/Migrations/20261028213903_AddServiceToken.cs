using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Tenancy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_tokens",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_tokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_tokens_TenantId",
                schema: "tenancy",
                table: "service_tokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_service_tokens_TokenHash",
                schema: "tenancy",
                table: "service_tokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_tokens",
                schema: "tenancy");
        }
    }
}
