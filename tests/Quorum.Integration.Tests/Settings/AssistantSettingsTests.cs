using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Settings;

public sealed class AssistantSettingsTests(PostgresFixture postgres)
{
    private static readonly Uri Settings = new("/api/settings/assistant", UriKind.Relative);
    private static readonly Uri TestConnection = new("/api/settings/assistant/test", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReturnsTheConfigurationChosenInTheWizard()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(systemPrompt: "Be brief.", contextTokenBudget: 8192);

        var settings = await client.GetFromJsonAsync<SettingsBody>(Settings, Ct);

        Assert.Equal(new SettingsBody(QuorumApp.ModelBaseUrl, "llama3.2", false, 8192, "Be brief."), settings);
    }

    [Fact] // AC-24
    public async Task ChangesApplyToTheNextMessages()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        app.ModelServer.Serve("http://vllm.test:8000/v1", "qwen2.5:14b");

        using var update = await client.PutAsJsonAsync(
            Settings,
            new { baseUrl = "http://vllm.test:8000/v1", modelName = "qwen2.5:14b", apiKey = "sk-new", contextTokenBudget = 16384, systemPrompt = "Answer in haiku." },
            Ct);
        var conversation = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, conversation, "Hello");

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var request = app.ModelServer.ChatRequests[^1];
        Assert.Equal("qwen2.5:14b", request.GetProperty("model").GetString());
        Assert.Equal("Answer in haiku.", request.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Bearer sk-new", app.ModelServer.LastAuthorization);
    }

    [Fact] // AC-25
    public async Task TheApiKeyIsNeverReturnedAndIsKeptWhenOmitted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var withKey = await client.PutAsJsonAsync(Settings, new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "llama3.2", apiKey = "sk-secret", contextTokenBudget = 4096, systemPrompt = "x" }, Ct);
        using var withoutKey = await client.PutAsJsonAsync(Settings, new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "llama3.2", contextTokenBudget = 4096, systemPrompt = "y" }, Ct);
        var raw = await client.GetStringAsync(Settings, Ct);

        Assert.DoesNotContain("sk-secret", raw, StringComparison.Ordinal);
        Assert.Contains("\"hasApiKey\":true", raw, StringComparison.Ordinal);

        using var cleared = await client.PutAsJsonAsync(Settings, new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "llama3.2", apiKey = "", contextTokenBudget = 4096, systemPrompt = "y" }, Ct);
        var afterClear = await client.GetFromJsonAsync<SettingsBody>(Settings, Ct);
        Assert.False(afterClear!.HasApiKey);
    }

    [Fact]
    public async Task InvalidSettingsAreRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.PutAsJsonAsync(Settings, new { baseUrl = "nope", modelName = "", contextTokenBudget = 10, systemPrompt = "x" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConnectionTestUsesTheStoredKeyWhenNoneIsGiven()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        using var _ = await client.PutAsJsonAsync(Settings, new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "llama3.2", apiKey = "sk-stored", contextTokenBudget = 4096, systemPrompt = "x" }, Ct);

        using var reachable = await client.PostAsJsonAsync(TestConnection, new { baseUrl = QuorumApp.ModelBaseUrl }, Ct);
        Assert.Equal("Bearer sk-stored", app.ModelServer.LastAuthorization);
        using var unreachable = await client.PostAsJsonAsync(TestConnection, new { baseUrl = "http://nowhere.test/v1" }, Ct);

        Assert.Equal(HttpStatusCode.OK, reachable.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unreachable.StatusCode);
    }

    private sealed record SettingsBody(string BaseUrl, string ModelName, bool HasApiKey, int ContextTokenBudget, string SystemPrompt);
}
