using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Models.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "models");

            migrationBuilder.CreateTable(
                name: "models",
                schema: "models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ModelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "text", nullable: true),
                    Capabilities = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContextTokenBudget = table.Column<int>(type: "integer", nullable: true),
                    QueryPrefix = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EmbeddingDimensions = table.Column<int>(type: "integer", nullable: true),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScopeTenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_models", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_models_TenantId_Name",
                schema: "models",
                table: "models",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "models",
                schema: "models");
        }
    }
}
