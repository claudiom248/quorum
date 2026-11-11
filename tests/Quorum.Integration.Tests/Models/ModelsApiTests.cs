using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Models;

/// <summary>The models of the installation: validation, names, the connection test and the permission (spec 019, AC-5, AC-6, AC-13, AC-15).</summary>
public sealed class ModelsApiTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    private static readonly Uri Models = new("/api/models", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static object Body(string name, string[] capabilities, int? budget = 4096, string? prefix = null, string modelName = "nomic") => new
    {
        name,
        baseUrl = QuorumApp.ModelBaseUrl,
        modelName,
        apiKey = (string?)null,
        capabilities,
        contextTokenBudget = budget,
        queryPrefix = prefix,
        scope = "Tenant",
        scopeTenantIds = Array.Empty<Guid>(),
    };

    [Fact] // AC-5
    public async Task AModelNeedsACapabilityToolsNeedChatAndChatNeedsABudget()
    {
        var client = host.Admin;

        using var none = await client.PostAsJsonAsync(Models, Body("none", []), Ct);
        using var toolsOnly = await client.PostAsJsonAsync(Models, Body("tools", ["tools"]), Ct);
        using var noBudget = await client.PostAsJsonAsync(Models, Body("budget", ["chat"], budget: null), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, toolsOnly.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noBudget.StatusCode);
    }

    [Fact] // AC-6
    public async Task ANameIsUniquePerTenantOnCreateAndRename()
    {
        var client = host.Admin;
        using var first = await client.PostAsJsonAsync(Models, Body("chat-model", ["chat"]), Ct);
        using var second = await client.PostAsJsonAsync(Models, Body("chat-model", ["chat"]), Ct);
        var other = await client.PostAsJsonAsync(Models, Body("other", ["chat"]), Ct);
        var otherId = (await other.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        using var renamed = await client.PutAsJsonAsync(new Uri($"/api/models/{otherId}", UriKind.Relative), Body("chat-model", ["chat"]), Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, renamed.StatusCode);
    }

    [Fact] // AC-13 (embeddings suggested with the dimensions; a chat-only server does not suggest them)
    public async Task TheConnectionTestSuggestsEmbeddingsWithTheirDimensions()
    {
        var client = host.Admin;
        host.App.ModelServer.Serve(QuorumApp.ModelBaseUrl, "nomic", "chat-only");
        host.App.ModelServer.ServeEmbeddings(QuorumApp.ModelBaseUrl, "nomic", _ => [0.1f, 0.2f, 0.3f]);

        using var embedding = await client.PostAsJsonAsync(new Uri("/api/models/test", UriKind.Relative), new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "nomic" }, Ct);
        using var chatOnly = await client.PostAsJsonAsync(new Uri("/api/models/test", UriKind.Relative), new { baseUrl = QuorumApp.ModelBaseUrl, modelName = "chat-only" }, Ct);
        var suggested = await embedding.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var refused = await chatOnly.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.OK, embedding.StatusCode);
        Assert.Contains("embeddings", suggested.GetProperty("suggestedCapabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(3, suggested.GetProperty("embeddingDimensions").GetInt32());
        Assert.DoesNotContain("embeddings", refused.GetProperty("suggestedCapabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.False(string.IsNullOrEmpty(refused.GetProperty("embeddingsProblem").GetString()));
    }

    [Fact] // AC-15: creating needs the permission; the admin lists and edits
    public async Task AMemberCannotCreateAModel()
    {
        var member = await host.App.CreateMemberClientAsync();

        using var response = await member.PostAsJsonAsync(Models, Body("member", ["chat"]), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AModelIsCreatedListedReadAndDeleted()
    {
        var client = host.Admin;
        using var created = await client.PostAsJsonAsync(Models, Body("main", ["chat", "tools"]), Ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var list = await client.GetFromJsonAsync<JsonElement>(Models, Ct);
        var read = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/models/{id}", UriKind.Relative), Ct);
        using var deleted = await client.DeleteAsync(new Uri($"/api/models/{id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Single(list.EnumerateArray());
        Assert.True(read.GetProperty("owned").GetBoolean());
        Assert.True(read.GetProperty("hasApiKey").ValueKind is JsonValueKind.False);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

}
