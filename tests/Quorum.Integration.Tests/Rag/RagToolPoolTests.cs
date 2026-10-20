using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.Rag;

/// <summary>Who can use a rag tool: the scope rules of the pool, what other tenants see, and what happens when a scope narrows (spec 003, AC-23, AC-43, AC-44, AC-48).</summary>
public sealed class RagToolPoolTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-23
    public async Task AnAssistantScopeOnlyPutsTheToolInThePoolOfTheAssistantsItLists()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var other = await AssistantFixtures.AddAsync(setup.App);
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(name: "for-other", knowledgeBaseIds: [kb], scope: new { kind = "Assistant", assistantIds = new[] { other } }));
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(name: "for-default", knowledgeBaseIds: [kb], scope: new { kind = "Assistant", assistantIds = new[] { setup.AssistantId } }));
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(name: "for-tenant", knowledgeBaseIds: [kb]));

        var defaultPool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool.Select(t => t.Name).Where(n => n.StartsWith("rag__", StringComparison.Ordinal)).Order().ToArray();
        var otherPool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, other)).Pool.Select(t => t.Name).Where(n => n.StartsWith("rag__", StringComparison.Ordinal)).Order().ToArray();

        Assert.Equal(["rag__for-default", "rag__for-tenant"], defaultPool);
        Assert.Equal(["rag__for-other", "rag__for-tenant"], otherPool);
    }

    [Fact] // AC-23
    public async Task ARagToolReachesTheModelOnlyWhenItIsActivated()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));
        var chat = await ChatApi.CreateConversationAsync(setup.Admin);
        setup.App.ModelServer.EnqueueRounds(FakeModelServer.ChatScript.Reply("one."), FakeModelServer.ChatScript.Reply("two."));
        await ToolFixtures.ActivateOnlyAsync(setup.Admin, "builtin__calculator");

        await ChatApi.SendAsync(setup.Admin, chat, "Hi");
        await RagApi.ActivateAsync(setup.Admin, "builtin__calculator", "rag__company");
        await ChatApi.SendAsync(setup.Admin, chat, "Again");

        string[] Offered(int request) =>
            [.. setup.App.ModelServer.ChatRequests[request].GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!)];
        Assert.Equal(["builtin__calculator"], Offered(0));
        Assert.Equal(["builtin__calculator", "rag__company"], Offered(1));
    }

    [Fact] // AC-23, AC-48
    public async Task AToolWithNoVisibleKnowledgeBaseIsNotInThePoolAndCannotBeActivated()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));
        using var delete = await setup.Admin.DeleteAsync(KnowledgeBaseApi.One(kb), Ct);
        delete.EnsureSuccessStatusCode();

        var pool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool;
        using var activate = await setup.Admin.PutAsJsonAsync(ToolsApi.AssistantTools(setup.AssistantId), new { toolIds = new[] { created.Id } }, Ct);

        Assert.DoesNotContain(pool, t => t.Name == "rag__company");
        Assert.Equal(HttpStatusCode.BadRequest, activate.StatusCode);
        Assert.Equal("tool.not_in_pool", await ToolsApi.ProblemCodeAsync(activate));
        // The tool itself is still there for the administrator to fix, with the knowledge base gone from it.
        var listed = Assert.Single(await RagApi.ListAsync(setup.Admin));
        Assert.Empty(listed.KnowledgeBases);
        Assert.Null((await RagApi.GetAsync(setup.Admin, created.Id)).Definition);
    }

    [Fact] // AC-44
    public async Task NarrowingTheScopeOfAKnowledgeBaseTakesItOutOfWhatTheToolOffers()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));
        await RagApi.ActivateAsync(setup.Admin, "rag__company");
        var narrower = await AssistantFixtures.AddAsync(setup.App);

        // The knowledge base is now only for another assistant: this one no longer sees it, so the tool has nothing to search here.
        var current = await KnowledgeBaseApi.GetAsync(setup.Admin, kb);
        using var narrow = await setup.Admin.PutAsJsonAsync(
            KnowledgeBaseApi.One(kb),
            KnowledgeBaseApi.Body(setup.Url, current.Connection!.Collection, "policies", scope: new { kind = "Assistant", assistantIds = new[] { narrower } }),
            Ct);
        narrow.EnsureSuccessStatusCode();

        var pool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool;
        setup.App.ModelServer.EnqueueRounds(FakeModelServer.ChatScript.Reply("No tool."));
        await ChatApi.SendAsync(setup.Admin, await ChatApi.CreateConversationAsync(setup.Admin), "Hello");

        Assert.DoesNotContain(pool, t => t.Name == "rag__company");
        Assert.False(setup.App.ModelServer.ChatRequests[0].TryGetProperty("tools", out _));
        // For the administrator the knowledge base is still part of the configuration.
        Assert.Equal([kb], (await RagApi.GetAsync(setup.Admin, created.Id)).KnowledgeBaseIds);
    }

    [Fact] // AC-43
    public async Task AnotherTenantsRagToolIsVisibleOnlyThroughItsScopeAndNeverWritable()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var stranger = Guid.CreateVersion7();
        var sharedKb = await AddForeignKnowledgeBaseAsync(setup.App, stranger, "shared-kb", KnowledgeBaseScope.Global);
        var privateKb = await AddForeignKnowledgeBaseAsync(setup.App, stranger, "private-kb", KnowledgeBaseScope.Tenant);

        var global = await AddForeignRagToolAsync(setup.App, stranger, "shared-global", [sharedKb], ToolScopeKind.Global);
        await AddForeignRagToolAsync(setup.App, stranger, "shared-us", [sharedKb], ToolScopeKind.Tenant, tenantIds: [setup.TenantId]);
        await AddForeignRagToolAsync(setup.App, stranger, "elsewhere", [sharedKb], ToolScopeKind.Tenant, tenantIds: [Guid.NewGuid()]);
        await AddForeignRagToolAsync(setup.App, stranger, "assistants", [sharedKb], ToolScopeKind.Assistant, assistantIds: [setup.AssistantId]);
        // Shared with everyone, but over a knowledge base this tenant cannot see: listed, yet there is nothing to search.
        await AddForeignRagToolAsync(setup.App, stranger, "blind", [privateKb], ToolScopeKind.Global);

        var listed = await RagApi.ListAsync(setup.Admin);
        var tools = (await ToolsApi.ListToolsAsync(setup.Admin)).Where(t => t.Origin == "Rag").Select(t => t.Name).Order().ToArray();
        var pool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool.Select(t => t.Name).Where(n => n.StartsWith("rag__", StringComparison.Ordinal)).Order().ToArray();
        using var read = await setup.Admin.GetAsync(RagApi.One(global), Ct);
        using var edit = await setup.Admin.PutAsJsonAsync(RagApi.One(global), RagApi.Body(name: "mine-now", knowledgeBaseIds: [sharedKb]), Ct);
        using var delete = await setup.Admin.DeleteAsync(RagApi.One(global), Ct);
        using var change = await setup.Admin.PutAsJsonAsync(ToolsApi.Tool(global), new { state = "Disabled", policy = "Auto" }, Ct);

        Assert.Equal(["blind", "shared-global", "shared-us"], listed.Select(t => t.Name).Order().ToArray());
        Assert.All(listed, t => Assert.True(t.Shared));
        // A shared tool shows nothing of its binding.
        Assert.All(listed, t => Assert.Empty(t.KnowledgeBases));
        Assert.All(listed, t => Assert.Empty(t.Scope.TenantIds));
        Assert.Equal(["blind", "shared-global", "shared-us"], tools);
        Assert.Equal(["rag__shared-global", "rag__shared-us"], pool);
        Assert.All([read, edit, delete, change], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    [Fact]
    public async Task ADisabledRagToolIsInNoPoolAndASharedOneOnlyWhileTheOwnerKeepsItEnabled()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var stranger = Guid.CreateVersion7();
        var sharedKb = await AddForeignKnowledgeBaseAsync(setup.App, stranger, "shared-kb", KnowledgeBaseScope.Global);
        await AddForeignRagToolAsync(setup.App, stranger, "off", [sharedKb], ToolScopeKind.Global, state: ToolState.Disabled);
        await AddForeignRagToolAsync(setup.App, stranger, "review", [sharedKb], ToolScopeKind.Global, state: ToolState.PendingReview);

        var pool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool.Select(t => t.Name);

        Assert.DoesNotContain(pool, name => name.StartsWith("rag__", StringComparison.Ordinal));
        Assert.Empty(await RagApi.ListAsync(setup.Admin));
    }

    private static async Task<Guid> AddForeignKnowledgeBaseAsync(QuorumApp app, Guid owner, string name, KnowledgeBaseScope scope)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var knowledgeBase = KnowledgeBaseFixtures.Create(owner, name, "http://qdrant.other-tenant.test:6334", "secret_collection", scope, []);
        db.KnowledgeBases.Add(knowledgeBase);
        await db.SaveChangesAsync(Ct);
        return knowledgeBase.Id;
    }

    /// <summary>A rag tool of another tenant, written straight to the database: the API has no way to create a second tenant yet.</summary>
    private static async Task<Guid> AddForeignRagToolAsync(
        QuorumApp app,
        Guid owner,
        string name,
        IEnumerable<Guid> knowledgeBaseIds,
        ToolScopeKind scope,
        IEnumerable<Guid>? tenantIds = null,
        IEnumerable<Guid>? assistantIds = null,
        ToolState state = ToolState.Enabled)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var configuration = new RagConfiguration([.. knowledgeBaseIds], [], "{}", [], 5, null, 6000, scope, [.. tenantIds ?? []], [.. assistantIds ?? []]);
        var tool = Tool.CreateRag(owner, name, "A tool of another tenant.", "hash-" + name, ToolPolicy.Auto);
        if (state != ToolState.Enabled)
        {
            tool.Review(state, ToolPolicy.Auto);
        }

        db.Tools.Add(tool);
        db.RagTools.Add(RagTool.Create(tool.Id, owner, configuration, now));
        await db.SaveChangesAsync(Ct);
        return tool.Id;
    }
}
