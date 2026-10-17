using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Integration.Tests.Tools;

public sealed class BuiltinToolProvisioningTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-16
    public async Task ANewSetupActivatesTheBuiltinToolsOnTheDefaultAssistant()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        var tools = await ToolsApi.ListToolsAsync(client);
        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;

        Assert.Equal(["ask_user", "calculator", "current_datetime"], tools.Select(t => t.Name).Order().ToArray());
        Assert.All(tools, tool =>
        {
            Assert.Equal("Builtin", tool.Origin);
            Assert.Null(tool.ServerName);
            Assert.Equal("Enabled", tool.State);
            Assert.Equal("Auto", tool.Policy);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        });
        Assert.Equal(["builtin__ask_user", "builtin__calculator", "builtin__current_datetime"], pool.Select(t => t.Name).Order().ToArray());
        Assert.All(pool, tool => Assert.True(tool.Active));
    }

    [Fact] // AC-16
    public async Task AnUpgradedInstallationGetsTheBuiltinToolsInactive()
    {
        await using var first = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var _ = await first.SetUpAsync();
        await ExecuteAsync(first.ConnectionString, "DELETE FROM agents.assistant_tools; DELETE FROM agents.tools; DELETE FROM agents.tool_settings;");

        await using var upgraded = first.Restart();
        using var client = upgraded.CreateUiClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), new { email = QuorumApp.AdminEmail, password = QuorumApp.AdminPassword }, Ct);
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        var tools = await ToolsApi.ListToolsAsync(client);
        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;
        var settings = await client.GetFromJsonAsync<ToolsApi.ToolSettingsBody>(ToolsApi.ToolSettings, Ct);

        Assert.Equal(3, tools.Count);
        Assert.Equal(3, pool.Count);
        Assert.All(pool, tool => Assert.False(tool.Active));
        Assert.Equal(new ToolsApi.ToolSettingsBody(8, 300, 30, 300, 20_000, 90), settings);
    }

    [Fact]
    public async Task RestartingKeepsTheToolsAndWhatTheAdminChanged()
    {
        await using var first = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await first.SetUpAsync();
        var calculator = (await ToolsApi.ListToolsAsync(admin)).Single(t => t.Name == "calculator");
        using var changed = await admin.PutAsJsonAsync(ToolsApi.Tool(calculator.Id), new { state = "Disabled", policy = "Confirm" }, Ct);
        changed.EnsureSuccessStatusCode();

        await using var restarted = first.Restart();
        await using var again = restarted.Restart();
        using var client = again.CreateUiClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), new { email = QuorumApp.AdminEmail, password = QuorumApp.AdminPassword }, Ct);

        var tools = await ToolsApi.ListToolsAsync(client);

        Assert.Equal(3, tools.Count);
        var kept = tools.Single(t => t.Name == "calculator");
        Assert.Equal(calculator.Id, kept.Id);
        Assert.Equal("Disabled", kept.State);
        Assert.Equal("Confirm", kept.Policy);
    }

    [Fact] // spec 003: the origin Native was renamed Builtin
    public async Task AnInstallationThatStoredTheOldOriginKeepsItsToolsAndWhatTheAdminChanged()
    {
        await using var first = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await first.SetUpAsync();
        var calculator = (await ToolsApi.ListToolsAsync(admin)).Single(t => t.Name == "calculator");
        using var changed = await admin.PutAsJsonAsync(ToolsApi.Tool(calculator.Id), new { state = "Disabled", policy = "Confirm" }, Ct);
        changed.EnsureSuccessStatusCode();

        // The database as the previous release left it: the old value stored and the migration not yet applied.
        await ExecuteAsync(
            first.ConnectionString,
            "UPDATE agents.tools SET \"Origin\" = 'Native'; DELETE FROM agents.\"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%RenameNativeOriginToBuiltin';");

        await using var upgraded = first.Restart();
        using var client = upgraded.CreateUiClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), new { email = QuorumApp.AdminEmail, password = QuorumApp.AdminPassword }, Ct);
        var tools = await ToolsApi.ListToolsAsync(client);

        Assert.Equal(3, tools.Count);
        Assert.All(tools, tool => Assert.Equal("Builtin", tool.Origin));
        var kept = tools.Single(t => t.Name == "calculator");
        Assert.Equal(calculator.Id, kept.Id);
        Assert.Equal("Disabled", kept.State);
        Assert.Equal("Confirm", kept.Policy);
    }

    [Fact] // AC-18
    public async Task AnotherTenantsToolsAreNotVisible()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            await BuiltinToolProvisioner.EnsureAsync(db, Guid.CreateVersion7(), Ct);
        }

        var visible = await ToolsApi.ListToolsAsync(client);

        Assert.Equal(3, visible.Count);
        await using var verification = new NpgsqlConnection(app.ConnectionString);
        await verification.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM agents.tools", verification);
        Assert.Equal(6L, await count.ExecuteScalarAsync(Ct));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
