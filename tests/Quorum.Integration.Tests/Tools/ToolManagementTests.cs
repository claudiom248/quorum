using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Tools;

public sealed class ToolManagementTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-8 (built-in tools start enabled; pending review arrives with MCP servers)
    public async Task TheAdminCanDisableAToolAndChangeItsPolicy()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var calculator = (await ToolsApi.ListToolsAsync(client)).Single(t => t.Name == "calculator");

        using var update = await client.PutAsJsonAsync(ToolsApi.Tool(calculator.Id), new { state = "Disabled", policy = "Confirm" }, Ct);
        var after = (await ToolsApi.ListToolsAsync(client)).Single(t => t.Id == calculator.Id);
        using var restore = await client.PutAsJsonAsync(ToolsApi.Tool(calculator.Id), new { state = "enabled", policy = "auto" }, Ct);
        var restored = (await ToolsApi.ListToolsAsync(client)).Single(t => t.Id == calculator.Id);

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(("Disabled", "Confirm"), (after.State, after.Policy));
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        Assert.Equal(("Enabled", "Auto"), (restored.State, restored.Policy));
    }

    [Theory]
    [InlineData("PendingReview", "Auto")]
    [InlineData("Enabled", "Sometimes")]
    [InlineData("", "Auto")]
    public async Task InvalidStatesAndPoliciesAreRejected(string state, string policy)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var tool = (await ToolsApi.ListToolsAsync(client))[0];

        using var response = await client.PutAsJsonAsync(ToolsApi.Tool(tool.Id), new { state, policy }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangingAnUnknownToolReturns404()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.PutAsJsonAsync(ToolsApi.Tool(Guid.NewGuid()), new { state = "Enabled", policy = "Auto" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ToolSettingsHaveTheSpecDefaultsAndCanBeChanged()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        var defaults = await client.GetFromJsonAsync<ToolsApi.ToolSettingsBody>(ToolsApi.ToolSettings, Ct);
        using var update = await client.PutAsJsonAsync(
            ToolsApi.ToolSettings,
            new { maxRounds = 4, totalTimeoutSeconds = 120, callTimeoutSeconds = 15, confirmationTimeoutSeconds = 600, maxResultCharacters = 5000, auditRetentionDays = 30 },
            Ct);
        var changed = await client.GetFromJsonAsync<ToolsApi.ToolSettingsBody>(ToolsApi.ToolSettings, Ct);

        Assert.Equal(new ToolsApi.ToolSettingsBody(8, 300, 30, 300, 20_000, 90), defaults);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(new ToolsApi.ToolSettingsBody(4, 120, 15, 600, 5000, 30), changed);
    }

    [Theory]
    [InlineData(0, 300, 30, 300, 20_000, 90)]
    [InlineData(8, 5, 30, 300, 20_000, 90)]
    [InlineData(8, 300, 0, 300, 20_000, 90)]
    [InlineData(8, 300, 30, 5, 20_000, 90)]
    [InlineData(8, 300, 30, 300, 10, 90)]
    [InlineData(8, 300, 30, 300, 20_000, 0)]
    public async Task OutOfRangeToolSettingsAreRejected(int rounds, int total, int call, int confirmation, int result, int retention)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.PutAsJsonAsync(
            ToolsApi.ToolSettings,
            new { maxRounds = rounds, totalTimeoutSeconds = total, callTimeoutSeconds = call, confirmationTimeoutSeconds = confirmation, maxResultCharacters = result, auditRetentionDays = retention },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
