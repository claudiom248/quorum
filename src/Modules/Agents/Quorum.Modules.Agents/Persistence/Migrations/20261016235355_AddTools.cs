using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SupportsTools",
                schema: "agents",
                table: "model_endpoints",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "tool_audit_entries",
                schema: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Arguments = table.Column<string>(type: "text", nullable: false),
                    ArgumentsTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    ResultCharacters = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_audit_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tool_settings",
                schema: "agents",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaxRounds = table.Column<int>(type: "integer", nullable: false),
                    TotalTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    CallTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    ConfirmationTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxResultCharacters = table.Column<int>(type: "integer", nullable: false),
                    AuditRetentionDays = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_settings", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "tools",
                schema: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    InputSchema = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Policy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "assistant_tools",
                schema: "agents",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_tools", x => new { x.AssistantId, x.ToolId });
                    table.ForeignKey(
                        name: "FK_assistant_tools_assistants_AssistantId",
                        column: x => x.AssistantId,
                        principalSchema: "agents",
                        principalTable: "assistants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assistant_tools_tools_ToolId",
                        column: x => x.ToolId,
                        principalSchema: "agents",
                        principalTable: "tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_tools_TenantId",
                schema: "agents",
                table: "assistant_tools",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_tools_ToolId",
                schema: "agents",
                table: "assistant_tools",
                column: "ToolId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_audit_entries_TenantId_StartedAt",
                schema: "agents",
                table: "tool_audit_entries",
                columns: new[] { "TenantId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_audit_entries_TenantId_ToolName",
                schema: "agents",
                table: "tool_audit_entries",
                columns: new[] { "TenantId", "ToolName" });

            migrationBuilder.CreateIndex(
                name: "IX_tools_TenantId_Origin_Name",
                schema: "agents",
                table: "tools",
                columns: new[] { "TenantId", "Origin", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_tools",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "tool_audit_entries",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "tool_settings",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "tools",
                schema: "agents");

            migrationBuilder.DropColumn(
                name: "SupportsTools",
                schema: "agents",
                table: "model_endpoints");
        }
    }
}
