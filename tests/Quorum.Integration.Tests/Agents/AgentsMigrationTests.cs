using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Integration.Tests.Agents;

/// <summary>The migration that turns assistants into root agents keeps what an assistant had (spec 013, AC-1).</summary>
public sealed class AgentsMigrationTests(PostgresFixture postgres)
{
    private const string BeforeAgents = "20261103013959_AddModelEndpointName";

    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid OtherTenant = Guid.CreateVersion7();
    private static readonly Guid Endpoint = Guid.CreateVersion7();
    private static readonly Guid OtherEndpoint = Guid.CreateVersion7();
    private static readonly Guid FirstAssistant = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid SecondAssistant = Guid.Parse("00000000-0000-7000-8000-000000000002");
    private static readonly Guid ThirdAssistant = Guid.CreateVersion7();
    private static readonly Guid Calculator = Guid.CreateVersion7();
    private static readonly Guid Lookup = Guid.CreateVersion7();
    private static readonly Guid Server = Guid.CreateVersion7();
    private static readonly Guid RagTool = Guid.CreateVersion7();

    [Fact] // AC-1
    public async Task EachAssistantBecomesARootAgentWithItsPromptEndpointToolsAndScopes()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var db = NewContext(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeAgents, TestContext.Current.CancellationToken);
        }

        await SeedOldSchemaAsync(connectionString);

        await using (var db = NewContext(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var agents = await RowsAsync(
            connection,
            """
            SELECT a."Id", a."Name", g."Slug", g."DisplayName", g."SystemPrompt", g."ModelEndpointId", g."Scope", g."TenantId", g."Id"
            FROM agents.assistants a JOIN agents.agents g ON g."Id" = a."RootAgentId"
            ORDER BY a."Id"
            """);
        Assert.Equal(3, agents.Count);

        // Same prompt and endpoint as before, one agent per assistant, owned by the assistant's tenant.
        Assert.Equal((FirstAssistant, "Support Desk", "support-desk", "Support Desk", "Answer as the first.", Endpoint, "Tenant", Tenant), Head(agents[0]));
        Assert.Equal((SecondAssistant, "Support Desk", "support-desk-2", "Support Desk", "Answer as the second.", OtherEndpoint, "Tenant", Tenant), Head(agents[1]));
        Assert.Equal((ThirdAssistant, "Légal & Co.", "l-gal-co", "Légal & Co.", "Answer as the third.", Endpoint, "Tenant", OtherTenant), Head(agents[2]));
        var firstAgent = (Guid)agents[0][8];
        var secondAgent = (Guid)agents[1][8];
        Assert.Equal(3, agents.Select(a => (Guid)a[8]).Distinct().Count());
        Assert.Equal(1, await ScalarAsync<int>(connection, "SELECT min(\"MaxParallelSubAgents\") FROM agents.assistants"));

        // The activations moved with their manifests, to the right agent.
        var tools = await RowsAsync(connection, "SELECT \"AgentId\", \"ToolId\", \"Manifest\" FROM agents.agent_tools ORDER BY \"AgentId\", \"ToolId\"");
        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => (Guid)t[0] == firstAgent && (Guid)t[1] == Calculator && (string)t[2] == "Full");
        Assert.Contains(tools, t => (Guid)t[0] == firstAgent && (Guid)t[1] == Lookup && (string)t[2] == "Lean");
        Assert.Contains(tools, t => (Guid)t[0] == secondAgent && (Guid)t[1] == Calculator && (string)t[2] == "Full");

        // A scope that named assistants names their root agents; a scope of any other kind is untouched.
        var server = await RowsAsync(connection, "SELECT \"Scope\", \"ScopeAgentIds\" FROM agents.mcp_servers WHERE \"Id\" = '" + Server + "'");
        Assert.Equal("Agent", (string)server.Single()[0]);
        Assert.Equal([firstAgent], (Guid[])server.Single()[1]);
        var rag = await RowsAsync(connection, "SELECT \"Scope\", \"ScopeAgentIds\", \"ScopeTenantIds\" FROM agents.rag_tools WHERE \"ToolId\" = '" + RagTool + "'");
        Assert.Equal("Tenant", (string)rag.Single()[0]);
        Assert.Empty((Guid[])rag.Single()[1]);
        Assert.Equal([OtherTenant], (Guid[])rag.Single()[2]);

        // What the assistant carried before is gone from it.
        Assert.Equal(0L, await ScalarAsync<long>(
            connection,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'agents' AND table_name = 'assistants' AND column_name IN ('SystemPrompt', 'ModelEndpointId')"));
        Assert.Equal(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'agents' AND table_name = 'assistant_tools'"));
    }

    [Fact]
    public async Task TheMigrationCanBeRevertedKeepingTheRootAgentsPromptAndTools()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var db = NewContext(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeAgents, TestContext.Current.CancellationToken);
        }

        await SeedOldSchemaAsync(connectionString);

        await using (var db = NewContext(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
            await db.GetService<IMigrator>().MigrateAsync(BeforeAgents, TestContext.Current.CancellationToken);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var assistants = await RowsAsync(connection, "SELECT \"Id\", \"SystemPrompt\", \"ModelEndpointId\" FROM agents.assistants ORDER BY \"Id\"");
        var activations = await ScalarAsync<long>(connection, "SELECT count(*) FROM agents.assistant_tools");
        var scope = await RowsAsync(connection, "SELECT \"Scope\", \"ScopeAssistantIds\" FROM agents.mcp_servers WHERE \"Id\" = '" + Server + "'");

        Assert.Equal(["Answer as the first.", "Answer as the second.", "Answer as the third."], assistants.Select(a => (string)a[1]));
        Assert.Equal([Endpoint, OtherEndpoint, Endpoint], assistants.Select(a => (Guid)a[2]));
        Assert.Equal(3L, activations);
        Assert.Equal("Assistant", (string)scope.Single()[0]);
        Assert.Equal([FirstAssistant], (Guid[])scope.Single()[1]);
    }

    private static (Guid, string, string, string, string, Guid, string, Guid) Head(object[] row) =>
        ((Guid)row[0], (string)row[1], (string)row[2], (string)row[3], (string)row[4], (Guid)row[5], (string)row[6], (Guid)row[7]);

    private static AgentsDbContext NewContext(string connectionString) =>
        new(
            new DbContextOptionsBuilder<AgentsDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, AgentsDbContext.Schema))
                .Options,
            NoTenantContext.Instance);

    private static async Task SeedOldSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // The schema as it was before agents: the prompt and the endpoint on the assistant, the activations in assistant_tools, scopes naming assistants.
        await using var command = new NpgsqlCommand(
            $$"""
            INSERT INTO agents.model_endpoints ("Id", "TenantId", "Name", "BaseUrl", "ModelName", "ContextTokenBudget", "SupportsTools", "UpdatedAt") VALUES
                ('{{Endpoint}}', '{{Tenant}}', 'llama3.2', 'http://model.test/v1', 'llama3.2', 8192, true, now()),
                ('{{OtherEndpoint}}', '{{Tenant}}', 'qwen', 'http://model.test/v1', 'qwen', 8192, true, now());

            INSERT INTO agents.assistants ("Id", "TenantId", "Name", "SystemPrompt", "ModelEndpointId") VALUES
                ('{{FirstAssistant}}', '{{Tenant}}', 'Support Desk', 'Answer as the first.', '{{Endpoint}}'),
                ('{{SecondAssistant}}', '{{Tenant}}', 'Support Desk', 'Answer as the second.', '{{OtherEndpoint}}'),
                ('{{ThirdAssistant}}', '{{OtherTenant}}', 'Légal & Co.', 'Answer as the third.', '{{Endpoint}}');

            INSERT INTO agents.tools ("Id", "TenantId", "Origin", "Name", "Description", "InputSchema", "State", "Policy") VALUES
                ('{{Calculator}}', '{{Tenant}}', 'Builtin', 'calculator', 'Adds up.', '{}', 'Enabled', 'Auto'),
                ('{{Lookup}}', '{{Tenant}}', 'Builtin', 'lookup', 'Looks up.', '{}', 'Enabled', 'Auto'),
                ('{{RagTool}}', '{{OtherTenant}}', 'Rag', 'docs', 'Searches.', '{}', 'Enabled', 'Auto');

            INSERT INTO agents.assistant_tools ("AssistantId", "ToolId", "TenantId", "Manifest") VALUES
                ('{{FirstAssistant}}', '{{Calculator}}', '{{Tenant}}', 'Full'),
                ('{{FirstAssistant}}', '{{Lookup}}', '{{Tenant}}', 'Lean'),
                ('{{SecondAssistant}}', '{{Calculator}}', '{{Tenant}}', 'Full');

            INSERT INTO agents.mcp_servers ("Id", "TenantId", "Name", "Url", "Status", "Scope", "ScopeTenantIds", "ScopeAssistantIds", "CreatedAt", "UpdatedAt")
                VALUES ('{{Server}}', '{{Tenant}}', 'files', 'http://mcp.test/', 'Available', 'Assistant', '{}', ARRAY['{{FirstAssistant}}', '{{Guid.NewGuid()}}']::uuid[], now(), now());
            """,
            connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // A rag tool needs its binding row; the columns it requires are the ones the model of that time had.
        await using var rag = new NpgsqlCommand(
            $$"""
            INSERT INTO agents.rag_tools ("ToolId", "TenantId", "KnowledgeBaseIds", "ExposedFieldIds", "FixedFilters", "FixedFilterFieldIds", "TopK", "MaxResultCharacters", "Scope", "ScopeTenantIds", "ScopeAssistantIds", "AllowDocumentIds", "CreatedAt", "UpdatedAt")
                VALUES ('{{RagTool}}', '{{OtherTenant}}', '{}', '{}', '{}', '{}', 5, 6000, 'Tenant', ARRAY['{{OtherTenant}}']::uuid[], '{}', false, now(), now());
            """,
            connection);
        await rag.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<object[]>> RowsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<object[]>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
