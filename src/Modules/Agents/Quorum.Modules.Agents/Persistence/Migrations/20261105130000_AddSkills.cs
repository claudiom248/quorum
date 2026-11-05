using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill_settings",
                schema: "agents",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayNameStyle = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_settings", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "skills",
                schema: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScopeTenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_skills",
                schema: "agents",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_skills", x => new { x.AgentId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_agent_skills_agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agents",
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_skills_skills_SkillId",
                        column: x => x.SkillId,
                        principalSchema: "agents",
                        principalTable: "skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_skills_SkillId",
                schema: "agents",
                table: "agent_skills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_skills_TenantId",
                schema: "agents",
                table: "agent_skills",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_skills_TenantId_Slug",
                schema: "agents",
                table: "skills",
                columns: new[] { "TenantId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_skills",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "skill_settings",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "skills",
                schema: "agents");
        }
    }
}
