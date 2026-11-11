using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;

namespace Quorum.Integration.Tests.Agents;

/// <summary>The consumer and admin routes of assistants and their tools (spec 018, AC-2, AC-4, AC-5, AC-9).</summary>
public sealed class AssistantRoutesTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    private static readonly Uri Assistants = new("/api/assistants", UriKind.Relative);
    private static readonly Uri AdminAssistants = new("/api/admin/assistants", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact] // AC-2
    public async Task AConsumerSeesOnlyTheExposedAssistantsAndGetsNotFoundForTheOther()
    {
        var admin = host.Admin;
        var first = await FirstAsync(admin);
        var slug = first.GetProperty("rootAgentSlug").GetString();
        var hidden = await CreateAsync(admin, new { name = "Hidden", rootAgentSlug = slug, exposed = false });
        var exposed = await CreateAsync(admin, new { name = "Exposed", rootAgentSlug = slug, exposed = true });
        var member = await host.App.CreateMemberClientAsync();

        var list = (await member.GetFromJsonAsync<JsonElement>(Assistants, Ct)).EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToHashSet();
        using var byId = await member.GetAsync(new Uri($"/api/assistants/{hidden}", UriKind.Relative), Ct);

        Assert.Equal(new HashSet<Guid> { first.GetProperty("id").GetGuid(), exposed }, list);
        Assert.DoesNotContain(hidden, list);
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    [Fact] // AC-4 (empty list)
    public async Task AnAssistantWithoutToolSelectionShowsNoToolsToConsumers()
    {
        var member = await host.App.CreateMemberClientAsync();
        var id = (await FirstAsync(host.Admin)).GetProperty("id").GetGuid();

        var tools = await member.GetFromJsonAsync<JsonElement>(new Uri($"/api/assistants/{id}/tools", UriKind.Relative), Ct);

        Assert.Empty(tools.EnumerateArray());
    }

    [Fact] // AC-5
    public async Task AConsumerSeesTheRootToolsWithTheirModesAndTheAdminSeesThemToo()
    {
        var admin = host.Admin;
        var first = await FirstAsync(admin);
        var id = first.GetProperty("id").GetGuid();
        using (var update = await admin.PutAsJsonAsync(
                   new Uri($"/api/assistants/{id}", UriKind.Relative),
                   new { name = first.GetProperty("name").GetString(), rootAgentSlug = first.GetProperty("rootAgentSlug").GetString(), allowToolSelection = true },
                   Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        }

        var agentId = await AssistantFixtures.RootAgentIdAsync(host.App, id);
        var pool = (await admin.GetFromJsonAsync<JsonElement>(new Uri($"/api/agents/{agentId}/tools", UriKind.Relative), Ct))
            .GetProperty("pool").EnumerateArray().Select(t => (ToolId: t.GetProperty("toolId").GetGuid(), Name: t.GetProperty("name").GetString()!)).ToList();
        var required = pool[0].ToolId;
        var optional = pool[1].ToolId;
        using (var attach = await admin.PutAsJsonAsync(
                   new Uri($"/api/agents/{agentId}/tools", UriKind.Relative),
                   new { tools = new object[] { new { toolId = required, mode = "required" }, new { toolId = optional, mode = "optional" } } },
                   Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, attach.StatusCode);
        }

        var member = await host.App.CreateMemberClientAsync();
        var consumer = (await member.GetFromJsonAsync<JsonElement>(new Uri($"/api/assistants/{id}/tools", UriKind.Relative), Ct))
            .EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("mode").GetString());
        var adminTools = (await admin.GetFromJsonAsync<JsonElement>(new Uri($"/api/admin/assistants/{id}/tools", UriKind.Relative), Ct))
            .EnumerateArray()
            .Count();

        Assert.Equal(2, consumer.Count);
        Assert.Equal("Required", consumer[pool[0].Name]);
        Assert.Equal("Optional", consumer[pool[1].Name]);
        Assert.Equal(2, adminTools);
    }

    [Fact] // AC-9
    public async Task TheAdminRoutesListEverythingAndRefuseOtherUsers()
    {
        var admin = host.Admin;
        var first = await FirstAsync(admin);
        var member = await host.App.CreateMemberClientAsync();

        var all = (await admin.GetFromJsonAsync<JsonElement>(AdminAssistants, Ct)).EnumerateArray().ToList();
        using var listDenied = await member.GetAsync(AdminAssistants, Ct);
        using var toolsDenied = await member.GetAsync(new Uri($"/api/admin/assistants/{first.GetProperty("id").GetGuid()}/tools", UriKind.Relative), Ct);

        Assert.Single(all);
        Assert.Equal(first.GetProperty("exposed").GetBoolean(), all[0].GetProperty("exposed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, listDenied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, toolsDenied.StatusCode);
    }

    private static async Task<JsonElement> FirstAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(AdminAssistants, Ct)).EnumerateArray().First();

    private static async Task<Guid> CreateAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync(Assistants, body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }
}
