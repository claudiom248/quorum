using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Agents;

/// <summary>Create, update and delete of assistants (spec 013, PR 4).</summary>
public sealed class AssistantApiTests(PostgresFixture postgres)
{
    private static readonly Uri Assistants = new("/api/assistants", UriKind.Relative);
    private static readonly Uri Agents = new("/api/agents", UriKind.Relative);
    private static readonly Uri Endpoints = new("/api/model-endpoints", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Assistant(Guid id) => new($"/api/assistants/{id}", UriKind.Relative);

    private static object Body(string name, string rootAgentSlug, int? maxParallelSubAgents = null) =>
        new { name, rootAgentSlug, maxParallelSubAgents };

    private static async Task<Guid> CreateAgentAsync(HttpClient client, string slug, bool withEndpoint)
    {
        Guid? endpoint = withEndpoint ? (await client.GetFromJsonAsync<List<IdBody>>(Endpoints, Ct))!.First().Id : null;
        using var response = await client.PostAsJsonAsync(
            Agents,
            new { slug, displayName = slug, description = $"Call {slug}.", systemPrompt = $"You are {slug}.", modelEndpointId = endpoint, scope = "Tenant", subAgents = Array.Empty<object>() },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdBody>(Ct))!.Id;
    }

    private static async Task<AssistantBody> CreateAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync(Assistants, body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AssistantBody>(Ct))!;
    }

    private static async Task<List<AssistantBody>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<AssistantBody>>(Assistants, Ct))!;

    [Fact]
    public async Task CreatesUpdatesAndListsAnAssistant()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await CreateAgentAsync(client, "legal", withEndpoint: true);
        await CreateAgentAsync(client, "finance", withEndpoint: true);

        var created = await CreateAsync(client, Body("Legal desk", "legal", maxParallelSubAgents: 3));
        using var update = await client.PutAsJsonAsync(Assistant(created.Id), Body("Finance desk", "finance"), Ct);
        var list = await ListAsync(client);

        Assert.Equal(("Legal desk", "legal", 3), (created.Name, created.RootAgentSlug, created.MaxParallelSubAgents));
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var updated = list.Single(a => a.Id == created.Id);
        Assert.Equal(("Finance desk", "finance", 3), (updated.Name, updated.RootAgentSlug, updated.MaxParallelSubAgents));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task TheLastAssistantCannotBeDeletedAndALimitLeftOutIsKept()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var only = (await ListAsync(client)).Single();

        using var refused = await client.DeleteAsync(Assistant(only.Id), Ct);
        using var update = await client.PutAsJsonAsync(Assistant(only.Id), Body(only.Name, "assistant", 4), Ct);
        using var rename = await client.PutAsJsonAsync(Assistant(only.Id), Body("Renamed", "assistant"), Ct);
        using var created = await client.PostAsJsonAsync(Assistants, Body("Second", "assistant"), Ct);
        var fetched = await client.GetFromJsonAsync<AssistantBody>(created.Headers.Location!, Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        Assert.Equal(4, (await ListAsync(client)).Single(a => a.Id == only.Id).MaxParallelSubAgents);
        Assert.Equal("Second", fetched!.Name);
    }

    [Fact] // AC-23
    public async Task ARootAgentWithoutAnEndpointIsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        await CreateAgentAsync(client, "inherits", withEndpoint: false);
        var existing = await CreateAsync(client, Body("Fine", "assistant"));

        using var create = await client.PostAsJsonAsync(Assistants, Body("Broken", "inherits"), Ct);
        using var update = await client.PutAsJsonAsync(Assistant(existing.Id), Body("Fine", "inherits"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.True((await ErrorsAsync(create)).TryGetProperty("rootAgentSlug", out _));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal("assistant", (await ListAsync(client)).Single(a => a.Id == existing.Id).RootAgentSlug);
    }

    [Fact]
    public async Task AnUnknownRootTheNameAndTheLimitAreChecked()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.PostAsJsonAsync(Assistants, Body(" ", "nobody", maxParallelSubAgents: 0), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            ["maxParallelSubAgents", "name", "rootAgentSlug"],
            (await ErrorsAsync(response)).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ChangingOrDeletingAnUnknownAssistantIsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var unknown = Assistant(Guid.NewGuid());

        using var put = await client.PutAsJsonAsync(unknown, Body("Ghost", "assistant"), Ct);
        using var delete = await client.DeleteAsync(unknown, Ct);

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact] // AC-27
    public async Task ADeletedAssistantKeepsReadableConversationsButRefusesNewMessages()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var conversation = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, conversation, "Before the assistant goes away");
        await CreateAsync(client, Body("Replacement", "assistant"));
        var original = (await ListAsync(client)).Single(a => a.Name != "Replacement");

        using var delete = await client.DeleteAsync(Assistant(original.Id), Ct);
        var history = await ChatApi.GetAsync(client, conversation);
        using var send = await client.PostAsJsonAsync(ChatApi.Messages(conversation), new { content = "Anyone there?" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);
        Assert.Equal("The assistant does not exist.", (await send.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        Assert.Equal(2, (await ChatApi.GetAsync(client, conversation)).Messages.Count);
    }

    [Fact] // AC-24
    public async Task WithoutAgentsManageTheWritesAreForbidden()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        var assistant = (await ListAsync(admin)).Single();

        var responses = new[]
        {
            await member.PostAsJsonAsync(Assistants, Body("Intruder", "assistant"), Ct),
            await member.PutAsJsonAsync(Assistant(assistant.Id), Body("Renamed", "assistant"), Ct),
            await member.DeleteAsync(Assistant(assistant.Id), Ct),
        };

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
            Assert.Equal(assistant.Name, Assert.Single(await ListAsync(admin)).Name);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");

    private sealed record IdBody(Guid Id);

    private sealed record AssistantBody(Guid Id, string Name, string RootAgentSlug, int MaxParallelSubAgents);
}
