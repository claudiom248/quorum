using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;

namespace Quorum.Integration.Tests.Chat;

/// <summary>Consumer and debug conversations: the selection they fix, what they return, and their routes (spec 018, AC-3, AC-4, AC-7, AC-8, AC-10, AC-13, AC-14, AC-15).</summary>
public sealed class ConversationSelectionApiTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    private static readonly Uri Conversations = new("/api/conversations", UriKind.Relative);
    private static readonly Uri AdminConversations = new("/api/admin/conversations", UriKind.Relative);

    private static readonly string[] CalculatorOnly = ["calculator"];

    private static readonly string[] CalculatorAndUnknown = ["calculator", "nope"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact] // AC-3
    public async Task CreatingAConversationWithoutAnAssistantIsRefused()
    {
        using var response = await host.Admin.PostAsJsonAsync(Conversations, new { }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-4 (400 on creation)
    public async Task ChoosingToolsOfAnAssistantThatDoesNotAllowItIsRefused()
    {
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(host.Admin);

        using var response = await host.Admin.PostAsJsonAsync(Conversations, new { assistantId, disabledTools = CalculatorOnly }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-7
    public async Task NamingRequiredOrUnknownToolsIsRefusedWithTheNames()
    {
        var admin = host.Admin;
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(admin);
        await AllowSelectionAsync(assistantId);
        await ToolFixtures.ActivateOnlyAsync(admin, "calculator");

        using var response = await admin.PostAsJsonAsync(Conversations, new { assistantId, disabledTools = CalculatorAndUnknown }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("calculator", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact] // AC-8
    public async Task AConsumerConversationReturnsItsSelectionAndNoDebugPart()
    {
        var admin = host.Admin;
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(admin);
        await AllowSelectionAsync(assistantId);
        await ToolFixtures.ActivateOnlyAsync(admin, "calculator");
        await SetModeAsync(admin, assistantId, "calculator", "optional");
        var id = await CreateConsumerAsync(admin, assistantId, ["calculator"]);

        var read = await admin.GetFromJsonAsync<JsonElement>(new Uri($"/api/conversations/{id}", UriKind.Relative), Ct);

        Assert.Equal(assistantId, read.GetProperty("assistantId").GetGuid());
        Assert.Equal(["calculator"], read.GetProperty("disabledTools").EnumerateArray().Select(t => t.GetString()));
        Assert.False(read.TryGetProperty("debug", out _));
    }

    [Fact] // AC-10
    public async Task ADebugConversationOnAHiddenAssistantIsListedOnlyByTheAdmin()
    {
        var admin = host.Admin;
        var slug = (await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/admin/assistants", UriKind.Relative), Ct))
            .EnumerateArray().First().GetProperty("rootAgentSlug").GetString();
        using var hidden = await admin.PostAsJsonAsync(new Uri("/api/assistants", UriKind.Relative), new { name = "Hidden", rootAgentSlug = slug, exposed = false }, Ct);
        var hiddenId = (await hidden.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        using var debug = await admin.PostAsJsonAsync(AdminConversations, new { assistantId = hiddenId }, Ct);
        var id = (await debug.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var consumer = (await admin.GetFromJsonAsync<JsonElement>(Conversations, Ct)).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        var debugList = (await admin.GetFromJsonAsync<JsonElement>(AdminConversations, Ct)).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(HttpStatusCode.Created, debug.StatusCode);
        Assert.DoesNotContain(id, consumer);
        Assert.Contains(id, debugList);
    }

    [Fact] // AC-13
    public async Task AnOverrideWithoutToolSupportIsRefusedWhenTheRootHasTools()
    {
        var admin = host.Admin;
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(admin);
        await ToolFixtures.ActivateOnlyAsync(admin, "calculator");
        var chatOnly = await CreateEndpointAsync(admin, "chat-only", supportsTools: false);

        using var response = await admin.PostAsJsonAsync(AdminConversations, new { assistantId, modelEndpointId = chatOnly }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact] // AC-14, AC-15
    public async Task TheOverridesOfADebugConversationChangeBetweenMessagesAndAConsumerConversationHasNone()
    {
        var admin = host.Admin;
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(admin);
        await AllowSelectionAsync(assistantId);
        await ToolFixtures.ActivateOnlyAsync(admin, "calculator");
        await SetModeAsync(admin, assistantId, "calculator", "optional");
        using var debug = await admin.PostAsJsonAsync(AdminConversations, new { assistantId }, Ct);
        var debugId = (await debug.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var consumerId = await CreateConsumerAsync(admin, assistantId, []);

        using var patched = await admin.PatchAsJsonAsync(new Uri($"/api/admin/conversations/{debugId}/overrides", UriKind.Relative), new { disabledTools = CalculatorOnly }, Ct);
        var read = await admin.GetFromJsonAsync<JsonElement>(new Uri($"/api/conversations/{debugId}", UriKind.Relative), Ct);
        using var onConsumer = await admin.PatchAsJsonAsync(new Uri($"/api/admin/conversations/{consumerId}/overrides", UriKind.Relative), new { disabledTools = Array.Empty<string>() }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);
        Assert.Equal(["calculator"], read.GetProperty("debug").GetProperty("disabledTools").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(HttpStatusCode.NotFound, onConsumer.StatusCode);
    }

    private async Task AllowSelectionAsync(Guid assistantId)
    {
        var admin = host.Admin;
        var current = await admin.GetFromJsonAsync<JsonElement>(new Uri($"/api/admin/assistants/{assistantId}", UriKind.Relative), Ct);
        using var response = await admin.PutAsJsonAsync(
            new Uri($"/api/assistants/{assistantId}", UriKind.Relative),
            new { name = current.GetProperty("name").GetString(), rootAgentSlug = current.GetProperty("rootAgentSlug").GetString(), allowToolSelection = true },
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task SetModeAsync(HttpClient client, Guid assistantId, string name, string mode)
    {
        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;
        var toolId = pool.Single(t => t.Name == name).ToolId;
        var agentId = await AssistantFixtures.RootAgentIdAsync(host.App, assistantId);
        using var response = await client.PutAsJsonAsync(
            new Uri($"/api/agents/{agentId}/tools", UriKind.Relative),
            new { tools = new[] { new { toolId, mode } } },
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<Guid> CreateConsumerAsync(HttpClient client, Guid assistantId, string[] disabled)
    {
        using var response = await client.PostAsJsonAsync(Conversations, new { assistantId, disabledTools = disabled }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateEndpointAsync(HttpClient client, string name, bool supportsTools)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/model-endpoints", UriKind.Relative),
            new { name, baseUrl = QuorumApp.ModelBaseUrl, modelName = name, apiKey = (string?)null, contextTokenBudget = 4096, supportsTools },
            Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }
}
