using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLookupTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lookup_tools",
                schema: "agents",
                columns: table => new
                {
                    ToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LookupIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    LookupAliases = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    KnowledgeBaseIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    ExposedFieldIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    ExposedAliases = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    FixedFilters = table.Column<string>(type: "jsonb", nullable: false),
                    FixedFilterFieldIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScopeTenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    ScopeAssistantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lookup_tools", x => x.ToolId);
                    table.ForeignKey(
                        name: "FK_lookup_tools_tools_ToolId",
                        column: x => x.ToolId,
                        principalSchema: "agents",
                        principalTable: "tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lookup_tools_TenantId",
                schema: "agents",
                table: "lookup_tools",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lookup_tools",
                schema: "agents");
        }
    }
}
