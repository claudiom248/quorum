using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Integration.Tests.Agents;

/// <summary>The migration that adds chat exposure keeps an installation behaving as before (spec 018, AC-1).</summary>
public sealed class ChatExposureMigrationTests(PostgresFixture postgres)
{
    private const string BeforeExposure = "20261105130000_AddSkills";

    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Endpoint = Guid.CreateVersion7();
    private static readonly Guid Agent = Guid.CreateVersion7();
    private static readonly Guid Assistant = Guid.CreateVersion7();

    [Fact] // AC-1
    public async Task ExistingAssistantsStayExposedWithToolSelectionOff()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var db = NewContext(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeExposure, TestContext.Current.CancellationToken);
        }

        await SeedOldSchemaAsync(connectionString);

        await using (var db = NewContext(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT \"Exposed\", \"AllowToolSelection\" FROM agents.assistants WHERE \"Id\" = '" + Assistant + "'",
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal((true, false), (reader.GetBoolean(0), reader.GetBoolean(1)));
        await reader.CloseAsync();

        // New attachments are required; the column default says so for every row written later (spec 018 D-19).
        await using var defaults = new NpgsqlCommand(
            "SELECT column_default FROM information_schema.columns WHERE table_schema = 'agents' AND table_name = 'agent_tools' AND column_name = 'Mode'",
            connection);
        Assert.Equal("'Required'::character varying", (string?)await defaults.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static AgentsDbContext NewContext(string connectionString) =>
        new(
            new DbContextOptionsBuilder<AgentsDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, AgentsDbContext.Schema))
                .Options,
            NoTenantContext.Instance);

    /// <summary>One assistant with its root agent and endpoint, as an installation had them before the change.</summary>
    private static async Task SeedOldSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $$"""
            INSERT INTO agents.model_endpoints ("Id", "TenantId", "Name", "BaseUrl", "ModelName", "ContextTokenBudget", "SupportsTools", "UpdatedAt") VALUES
                ('{{Endpoint}}', '{{Tenant}}', 'llama3.2', 'http://model.test/v1', 'llama3.2', 8192, true, now());

            INSERT INTO agents.agents ("Id", "TenantId", "Slug", "DisplayName", "Description", "SystemPrompt", "ModelEndpointId", "Scope", "ScopeTenantIds", "UpdatedAt") VALUES
                ('{{Agent}}', '{{Tenant}}', 'support-desk', 'Support Desk', 'Helps.', 'Answer.', '{{Endpoint}}', 'Tenant', '{}', now());

            INSERT INTO agents.assistants ("Id", "TenantId", "Name", "RootAgentId", "MaxParallelSubAgents") VALUES
                ('{{Assistant}}', '{{Tenant}}', 'Support Desk', '{{Agent}}', 1);
            """,
            connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
