using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgents : Migration
    {
        // Tables whose visibility scope names assistants before this migration and agents after it.
        private static readonly string[] ScopedTables = ["mcp_servers", "rag_tools", "lookup_tools", "web_search_tools", "web_fetch_tools"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                schema: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    ModelEndpointId = table.Column<Guid>(type: "uuid", nullable: true),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScopeTenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agents_model_endpoints_ModelEndpointId",
                        column: x => x.ModelEndpointId,
                        principalSchema: "agents",
                        principalTable: "model_endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_sub_agents",
                schema: "agents",
                columns: table => new
                {
                    ParentAgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildAgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FailurePolicy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "ReturnError")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_sub_agents", x => new { x.ParentAgentId, x.ChildAgentId });
                    table.ForeignKey(
                        name: "FK_agent_sub_agents_agents_ChildAgentId",
                        column: x => x.ChildAgentId,
                        principalSchema: "agents",
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_sub_agents_agents_ParentAgentId",
                        column: x => x.ParentAgentId,
                        principalSchema: "agents",
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_tools",
                schema: "agents",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Manifest = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "Full")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_tools", x => new { x.AgentId, x.ToolId });
                    table.ForeignKey(
                        name: "FK_agent_tools_agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agents",
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_tools_tools_ToolId",
                        column: x => x.ToolId,
                        principalSchema: "agents",
                        principalTable: "tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_sub_agents_ChildAgentId",
                schema: "agents",
                table: "agent_sub_agents",
                column: "ChildAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_sub_agents_TenantId",
                schema: "agents",
                table: "agent_sub_agents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_tools_TenantId",
                schema: "agents",
                table: "agent_tools",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_tools_ToolId",
                schema: "agents",
                table: "agent_tools",
                column: "ToolId");

            migrationBuilder.CreateIndex(
                name: "IX_agents_ModelEndpointId",
                schema: "agents",
                table: "agents",
                column: "ModelEndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_agents_TenantId_Slug",
                schema: "agents",
                table: "agents",
                columns: new[] { "TenantId", "Slug" },
                unique: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                schema: "agents",
                table: "tool_audit_entries",
                type: "uuid",
                nullable: true);

            // The columns that point at the agent stay nullable until the data is copied; the old ones stay until it is verified below.
            migrationBuilder.AddColumn<Guid>(
                name: "RootAgentId",
                schema: "agents",
                table: "assistants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxParallelSubAgents",
                schema: "agents",
                table: "assistants",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            foreach (var table in ScopedTables)
            {
                migrationBuilder.RenameColumn(
                    name: "ScopeAssistantIds",
                    schema: "agents",
                    table: table,
                    newName: "ScopeAgentIds");
            }

            // Each assistant becomes an agent holding its prompt and endpoint (spec 013 D-6). The slug comes from the assistant's name;
            // when two assistants of a tenant give the same one, the later ones get a numeric suffix.
            migrationBuilder.Sql("""
                UPDATE agents.assistants SET "RootAgentId" = gen_random_uuid();

                INSERT INTO agents.agents ("Id", "TenantId", "Slug", "DisplayName", "Description", "SystemPrompt", "ModelEndpointId", "Scope", "ScopeTenantIds", "UpdatedAt")
                SELECT
                    ranked."RootAgentId",
                    ranked."TenantId",
                    CASE WHEN ranked.rn = 1 THEN ranked.base_slug ELSE left(ranked.base_slug, 32 - length(ranked.rn::text) - 1) || '-' || ranked.rn END,
                    ranked."Name",
                    'The general-purpose assistant of the organization.',
                    ranked."SystemPrompt",
                    ranked."ModelEndpointId",
                    'Tenant',
                    '{}',
                    now()
                FROM (
                    SELECT
                        a."RootAgentId", a."TenantId", a."Name", a."SystemPrompt", a."ModelEndpointId", s.base_slug,
                        row_number() OVER (PARTITION BY a."TenantId", s.base_slug ORDER BY a."Id") AS rn
                    FROM agents.assistants AS a
                    CROSS JOIN LATERAL (
                        SELECT COALESCE(
                            NULLIF(trim(both '-' from left(trim(both '-' from regexp_replace(lower(a."Name"), '[^a-z0-9]+', '-', 'g')), 32)), ''),
                            'agent') AS base_slug
                    ) AS s
                ) AS ranked;

                INSERT INTO agents.agent_tools ("AgentId", "ToolId", "TenantId", "Manifest")
                SELECT a."RootAgentId", t."ToolId", t."TenantId", t."Manifest"
                FROM agents.assistant_tools AS t
                JOIN agents.assistants AS a ON a."Id" = t."AssistantId";
                """);

            // A scope that named assistants now names their root agents.
            foreach (var table in ScopedTables)
            {
                migrationBuilder.Sql($$"""
                    UPDATE agents.{{table}} AS t
                    SET "Scope" = 'Agent',
                        "ScopeAgentIds" = COALESCE((SELECT array_agg(a."RootAgentId") FROM agents.assistants AS a WHERE a."Id" = ANY(t."ScopeAgentIds")), '{}')
                    WHERE t."Scope" = 'Assistant';
                    """);
            }

            migrationBuilder.AlterColumn<Guid>(
                name: "RootAgentId",
                schema: "agents",
                table: "assistants",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistants_RootAgentId",
                schema: "agents",
                table: "assistants",
                column: "RootAgentId");

            migrationBuilder.AddForeignKey(
                name: "FK_assistants_agents_RootAgentId",
                schema: "agents",
                table: "assistants",
                column: "RootAgentId",
                principalSchema: "agents",
                principalTable: "agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropTable(
                name: "assistant_tools",
                schema: "agents");

            migrationBuilder.DropForeignKey(
                name: "FK_assistants_model_endpoints_ModelEndpointId",
                schema: "agents",
                table: "assistants");

            migrationBuilder.DropIndex(
                name: "IX_assistants_ModelEndpointId",
                schema: "agents",
                table: "assistants");

            migrationBuilder.DropColumn(
                name: "ModelEndpointId",
                schema: "agents",
                table: "assistants");

            migrationBuilder.DropColumn(
                name: "SystemPrompt",
                schema: "agents",
                table: "assistants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SystemPrompt",
                schema: "agents",
                table: "assistants",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ModelEndpointId",
                schema: "agents",
                table: "assistants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assistant_tools",
                schema: "agents",
                columns: table => new
                {
                    AssistantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Manifest = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "Full"),
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

            // Only the root agent of an assistant has a place in the old model: the other agents and the sub-agent edges are lost.
            migrationBuilder.Sql("""
                UPDATE agents.assistants AS a
                SET "SystemPrompt" = g."SystemPrompt", "ModelEndpointId" = g."ModelEndpointId"
                FROM agents.agents AS g
                WHERE g."Id" = a."RootAgentId";

                INSERT INTO agents.assistant_tools ("AssistantId", "ToolId", "TenantId", "Manifest")
                SELECT a."Id", t."ToolId", t."TenantId", t."Manifest"
                FROM agents.agent_tools AS t
                JOIN agents.assistants AS a ON a."RootAgentId" = t."AgentId";
                """);

            foreach (var table in ScopedTables)
            {
                migrationBuilder.Sql($$"""
                    UPDATE agents.{{table}} AS t
                    SET "Scope" = 'Assistant',
                        "ScopeAgentIds" = COALESCE((SELECT array_agg(a."Id") FROM agents.assistants AS a WHERE a."RootAgentId" = ANY(t."ScopeAgentIds")), '{}')
                    WHERE t."Scope" = 'Agent';
                    """);

                migrationBuilder.RenameColumn(
                    name: "ScopeAgentIds",
                    schema: "agents",
                    table: table,
                    newName: "ScopeAssistantIds");
            }

            migrationBuilder.AlterColumn<Guid>(
                name: "ModelEndpointId",
                schema: "agents",
                table: "assistants",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistants_ModelEndpointId",
                schema: "agents",
                table: "assistants",
                column: "ModelEndpointId");

            migrationBuilder.AddForeignKey(
                name: "FK_assistants_model_endpoints_ModelEndpointId",
                schema: "agents",
                table: "assistants",
                column: "ModelEndpointId",
                principalSchema: "agents",
                principalTable: "model_endpoints",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(
                name: "FK_assistants_agents_RootAgentId",
                schema: "agents",
                table: "assistants");

            migrationBuilder.DropIndex(
                name: "IX_assistants_RootAgentId",
                schema: "agents",
                table: "assistants");

            migrationBuilder.DropColumn(
                name: "RootAgentId",
                schema: "agents",
                table: "assistants");

            migrationBuilder.DropColumn(
                name: "MaxParallelSubAgents",
                schema: "agents",
                table: "assistants");

            migrationBuilder.DropColumn(
                name: "AgentId",
                schema: "agents",
                table: "tool_audit_entries");

            migrationBuilder.DropTable(
                name: "agent_sub_agents",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "agent_tools",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "agents",
                schema: "agents");
        }
    }
}
