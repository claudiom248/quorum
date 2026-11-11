using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Agents;

/// <summary>Exposure, tool selection and tool modes are stored and returned by the management API (spec 018, AC-18, AC-20).</summary>
public sealed class ChatExposureApiTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    private static readonly Uri Assistants = new("/api/assistants", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact] // AC-18
    public async Task TheFirstAssistantCannotBeHiddenFromConsumers()
    {
        var client = host.Admin;
        var first = await FirstAssistantAsync(client);
        var id = first.GetProperty("id").GetGuid();

        using var response = await client.PutAsJsonAsync(
            new Uri($"/api/assistants/{id}", UriKind.Relative),
            new { name = "Support desk", rootAgentSlug = first.GetProperty("rootAgentSlug").GetString(), exposed = false },
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact] // AC-20
    public async Task ExposureAndToolSelectionPersistWhenSentAndKeepTheirValueWhenLeftOut()
    {
        var client = host.Admin;
        var slug = (await FirstAssistantAsync(client)).GetProperty("rootAgentSlug").GetString();

        using var created = await client.PostAsJsonAsync(
            Assistants,
            new { name = "Legal desk", rootAgentSlug = slug, exposed = true, allowToolSelection = true },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        using var renamed = await client.PutAsJsonAsync(new Uri($"/api/assistants/{id}", UriKind.Relative), new { name = "Legal", rootAgentSlug = slug }, Ct);
        var stored = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/assistants/{id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal((true, true), (stored.GetProperty("exposed").GetBoolean(), stored.GetProperty("allowToolSelection").GetBoolean()));

        using var hidden = await client.PutAsJsonAsync(
            new Uri($"/api/assistants/{id}", UriKind.Relative),
            new { name = "Legal", rootAgentSlug = slug, exposed = false },
            Ct);
        var after = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/assistants/{id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);
        Assert.False(after.GetProperty("exposed").GetBoolean());
        Assert.True(after.GetProperty("allowToolSelection").GetBoolean());
    }

    [Fact] // AC-20
    public async Task ToolModesPersistAndAPlainListKeepsThem()
    {
        var client = host.Admin;
        var assistantId = (await FirstAssistantAsync(client)).GetProperty("id").GetGuid();
        var agentId = await AssistantFixtures.RootAgentIdAsync(host.App, assistantId);
        var agentTools = new Uri($"/api/agents/{agentId}/tools", UriKind.Relative);

        var pool = (await client.GetFromJsonAsync<JsonElement>(agentTools, Ct)).GetProperty("pool");
        var toolId = pool.EnumerateArray().First(t => t.GetProperty("active").GetBoolean()).GetProperty("toolId").GetGuid();

        using var optional = await client.PutAsJsonAsync(agentTools, new { tools = new[] { new { toolId, mode = "optional" } } }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, optional.StatusCode);
        Assert.Equal("Optional", ModeOf(await client.GetFromJsonAsync<JsonElement>(agentTools, Ct), toolId));

        using var plain = await client.PutAsJsonAsync(agentTools, new { toolIds = new[] { toolId } }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, plain.StatusCode);
        Assert.Equal("Optional", ModeOf(await client.GetFromJsonAsync<JsonElement>(agentTools, Ct), toolId));

        using var invalid = await client.PutAsJsonAsync(agentTools, new { tools = new[] { new { toolId, mode = "sometimes" } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static string ModeOf(JsonElement tools, Guid toolId) =>
        tools.GetProperty("pool").EnumerateArray().Single(t => t.GetProperty("toolId").GetGuid() == toolId).GetProperty("mode").GetString()!;

    private static async Task<JsonElement> FirstAssistantAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(Assistants, Ct)).EnumerateArray().First();
}
