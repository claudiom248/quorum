using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Setup;

namespace Quorum.Integration.Tests.Tools;

public sealed class ModelToolSupportTests(PostgresFixture postgres)
{
    private static readonly Uri Settings = new("/api/settings/assistant", UriKind.Relative);
    private static readonly Uri TestConnection = new("/api/settings/assistant/test", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory] // AC-30
    [InlineData(new[] { "completion", "tools" }, true)]
    [InlineData(new[] { "completion", "vision" }, false)]
    public async Task TheSetupTestReportsWhatTheServerSaysAboutTools(string[] capabilities, bool expected)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Serve(SetupApi.OllamaBaseUrl, "llama3.2");
        app.ModelServer.Describe(SetupApi.OllamaBaseUrl, "llama3.2", capabilities);
        using var client = app.CreateSetupClient();

        using var response = await client.PostAsJsonAsync(SetupApi.TestModel, new { baseUrl = SetupApi.OllamaBaseUrl, modelName = "llama3.2" }, Ct);
        var body = await response.Content.ReadFromJsonAsync<ConnectionBody>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, body!.SupportsTools);
    }

    [Fact] // AC-30
    public async Task TheSupportIsUnknownWhenTheServerDoesNotDescribeTheModel()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Serve(SetupApi.OllamaBaseUrl, "llama3.2");
        using var client = app.CreateSetupClient();

        using var withModel = await client.PostAsJsonAsync(SetupApi.TestModel, new { baseUrl = SetupApi.OllamaBaseUrl, modelName = "llama3.2" }, Ct);
        using var withoutModel = await client.PostAsJsonAsync(SetupApi.TestModel, new { baseUrl = SetupApi.OllamaBaseUrl }, Ct);

        Assert.Null((await withModel.Content.ReadFromJsonAsync<ConnectionBody>(Ct))!.SupportsTools);
        Assert.Null((await withoutModel.Content.ReadFromJsonAsync<ConnectionBody>(Ct))!.SupportsTools);
    }

    [Fact] // AC-30
    public async Task TheSettingsTestReportsTheSupportToo()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Describe(QuorumApp.ModelBaseUrl, "llama3.2", "completion", "tools");

        using var response = await client.PostAsJsonAsync(TestConnection, new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "llama3.2" }, Ct);
        var body = await response.Content.ReadFromJsonAsync<ConnectionBody>(Ct);

        Assert.True(body!.SupportsTools);
    }

    [Fact]
    public async Task TheFlagDefaultsToOnAndCanBeTurnedOffAndKeptWhenOmitted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        var initial = await client.GetFromJsonAsync<SettingsBody>(Settings, Ct);
        using var off = await client.PutAsJsonAsync(Settings, new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "llama3.2", contextTokenBudget = 4096, systemPrompt = "x", supportsTools = false }, Ct);
        var afterOff = await client.GetFromJsonAsync<SettingsBody>(Settings, Ct);
        using var omitted = await client.PutAsJsonAsync(Settings, new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "llama3.2", contextTokenBudget = 4096, systemPrompt = "y" }, Ct);
        var afterOmitted = await client.GetFromJsonAsync<SettingsBody>(Settings, Ct);

        Assert.True(initial!.SupportsTools);
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.False(afterOff!.SupportsTools);
        Assert.Equal(HttpStatusCode.NoContent, omitted.StatusCode);
        Assert.False(afterOmitted!.SupportsTools);
    }

    [Fact]
    public async Task TheWizardCanStoreTheDetectedValue()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Serve(SetupApi.OllamaBaseUrl, "llama3.2");
        using var client = app.CreateSetupClient();

        using var complete = await client.PostAsJsonAsync(
            SetupApi.Complete,
            new
            {
                admin = new { email = "admin@acme.test", displayName = "Acme Admin", password = SetupApi.ValidPassword },
                model = new { baseUrl = SetupApi.OllamaBaseUrl, modelName = "llama3.2", contextTokenBudget = 4096, supportsTools = false },
                systemPrompt = "x",
            },
            Ct);
        var settings = await client.GetFromJsonAsync<SettingsBody>(Settings, Ct);

        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        Assert.False(settings!.SupportsTools);
    }

    private sealed record ConnectionBody(bool Reachable, string[] Models, bool? SupportsTools);

    private sealed record SettingsBody(string BaseUrl, string ModelName, bool HasApiKey, int ContextTokenBudget, string SystemPrompt, bool SupportsTools);
}
