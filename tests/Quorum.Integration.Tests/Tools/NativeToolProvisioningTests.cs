using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Integration.Tests.Tools;

public sealed class NativeToolProvisioningTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-16
    public async Task ANewSetupActivatesTheNativeToolsOnTheDefaultAssistant()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);

        var tools = await ToolsApi.ListToolsAsync(client);
        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;

        Assert.Equal(["calculator", "current_datetime"], tools.Select(t => t.Name).Order().ToArray());
        Assert.All(tools, tool =>
        {
            Assert.Equal("Native", tool.Origin);
            Assert.Null(tool.ServerName);
            Assert.Equal("Enabled", tool.State);
            Assert.Equal("Auto", tool.Policy);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        });
        Assert.Equal(["native__calculator", "native__current_datetime"], pool.Select(t => t.Name).Order().ToArray());
        Assert.All(pool, tool => Assert.True(tool.Active));
    }

    [Fact] // AC-16
    public async Task AnUpgradedInstallationGetsTheNativeToolsInactive()
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

        Assert.Equal(2, tools.Count);
        Assert.Equal(2, pool.Count);
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

        Assert.Equal(2, tools.Count);
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
            await NativeToolProvisioner.EnsureAsync(db, Guid.CreateVersion7(), Ct);
        }

        var visible = await ToolsApi.ListToolsAsync(client);

        Assert.Equal(2, visible.Count);
        await using var verification = new NpgsqlConnection(app.ConnectionString);
        await verification.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM agents.tools", verification);
        Assert.Equal(4L, await count.ExecuteScalarAsync(Ct));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
