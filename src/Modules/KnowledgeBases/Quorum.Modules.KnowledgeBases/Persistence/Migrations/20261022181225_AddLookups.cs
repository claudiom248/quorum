using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.KnowledgeBases.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLookups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LookupRefreshHours",
                schema: "knowledge_base",
                table: "knowledge_bases",
                type: "integer",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.CreateTable(
                name: "lookups",
                schema: "knowledge_base",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    KnowledgeBaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    DictionaryFieldId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ScopeTenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lookups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lookups_knowledge_bases_KnowledgeBaseId",
                        column: x => x.KnowledgeBaseId,
                        principalSchema: "knowledge_base",
                        principalTable: "knowledge_bases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lookup_slices",
                schema: "knowledge_base",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LookupId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilterHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Filters = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Stale = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentGeneration = table.Column<int>(type: "integer", nullable: false),
                    ValueCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedValueCount = table.Column<int>(type: "integer", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NextRefreshAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ToolIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lookup_slices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lookup_slices_lookups_LookupId",
                        column: x => x.LookupId,
                        principalSchema: "knowledge_base",
                        principalTable: "lookups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lookup_values",
                schema: "knowledge_base",
                columns: table => new
                {
                    SliceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Synonyms = table.Column<List<string>>(type: "text[]", nullable: false),
                    DocumentCount = table.Column<int>(type: "integer", nullable: true),
                    Attributes = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lookup_values", x => new { x.SliceId, x.Generation, x.Position });
                    table.ForeignKey(
                        name: "FK_lookup_values_lookup_slices_SliceId",
                        column: x => x.SliceId,
                        principalSchema: "knowledge_base",
                        principalTable: "lookup_slices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lookup_slices_LookupId_FilterHash",
                schema: "knowledge_base",
                table: "lookup_slices",
                columns: new[] { "LookupId", "FilterHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lookup_slices_NextRefreshAt",
                schema: "knowledge_base",
                table: "lookup_slices",
                column: "NextRefreshAt");

            migrationBuilder.CreateIndex(
                name: "IX_lookup_values_SliceId_Generation_DocumentCount",
                schema: "knowledge_base",
                table: "lookup_values",
                columns: new[] { "SliceId", "Generation", "DocumentCount" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_lookup_values_SliceId_Generation_Value",
                schema: "knowledge_base",
                table: "lookup_values",
                columns: new[] { "SliceId", "Generation", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_lookups_KnowledgeBaseId_DictionaryFieldId",
                schema: "knowledge_base",
                table: "lookups",
                columns: new[] { "KnowledgeBaseId", "DictionaryFieldId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lookups_TenantId_Name",
                schema: "knowledge_base",
                table: "lookups",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lookup_values",
                schema: "knowledge_base");

            migrationBuilder.DropTable(
                name: "lookup_slices",
                schema: "knowledge_base");

            migrationBuilder.DropTable(
                name: "lookups",
                schema: "knowledge_base");

            migrationBuilder.DropColumn(
                name: "LookupRefreshHours",
                schema: "knowledge_base",
                table: "knowledge_bases");
        }
    }
}
