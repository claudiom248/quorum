using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quorum.Modules.Agents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameNativeOriginToBuiltin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The stored value is the enum's name: only the rows change, not the schema.
            migrationBuilder.Sql("UPDATE agents.tools SET \"Origin\" = 'Builtin' WHERE \"Origin\" = 'Native';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE agents.tools SET \"Origin\" = 'Native' WHERE \"Origin\" = 'Builtin';");
        }
    }
}
