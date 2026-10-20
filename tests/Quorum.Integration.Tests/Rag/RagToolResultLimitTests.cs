using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Rag;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Rag;

/// <summary>The size of a rag result against the tenant's limit on tool results, and what is told when the definition of a tool names a field that is gone.</summary>
public sealed class RagToolResultLimitTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AResultSizeThatCannotFitTheTenantsLimitOnToolResultsIsRejectedAtSaveAndAcceptedOnceTheLimitIsRaised()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        using var tooBig = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], maxResultCharacters: 30_000));
        await ToolFixtures.SetLimitsAsync(setup.Admin, maxResult: 60_000);
        using var raised = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], maxResultCharacters: 30_000));

        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Equal("rag.limit_exceeded", await ToolsApi.ProblemCodeAsync(tooBig));
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
    }

    [Fact]
    public async Task AToolSavedBeforeTheLimitWasLoweredAsksForLessAndStillAnswers()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], topK: 20, maxResultCharacters: 12_000));
        await RagApi.ActivateAsync(setup.Admin, "rag__company");
        // Lowered afterwards: twenty sources no longer fit with their formatting, so the search asks for less.
        await ToolFixtures.SetLimitsAsync(setup.Admin, maxResult: 6_700);
        setup.App.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("rag__company", JsonSerializer.Serialize(new { query = "policy" }), "call_1")),
            ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(setup.Admin, await ChatApi.CreateConversationAsync(setup.Admin), "Question");

        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal("succeeded", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("content").GetString()!.Length <= 6_700);
    }

    [Fact]
    public async Task ADefinitionMadeMomentsAgoIsReusedForTheContextGaugeButNeverForAnAnswer()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));
        await RagApi.ActivateAsync(setup.Admin, "rag__company");

        async Task<int> OfferedAsync(bool allowRecent)
        {
            await using var scope = TenantScope.Open(setup.App, setup.TenantId);
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var resolver = scope.ServiceProvider.GetRequiredService<RagToolResolver>();
            var active = await ActiveToolsQuery.LoadAsync(db, setup.AssistantId, Ct);
            var resolved = await resolver.ResolveActiveAsync(active, setup.TenantId, setup.AssistantId, Ct, allowRecent);
            return resolved.Count(t => t.Rag is not null);
        }

        Assert.Equal(1, await OfferedAsync(allowRecent: true));
        using var delete = await setup.Admin.DeleteAsync(KnowledgeBaseApi.One(kb), Ct);
        delete.EnsureSuccessStatusCode();

        // The gauge only needs the size and is asked for as the user types; an answer is made from what the catalog says now.
        Assert.Equal(1, await OfferedAsync(allowRecent: true));
        Assert.Equal(0, await OfferedAsync(allowRecent: false));
    }

    [Fact]
    public async Task AFieldDeletedBehindTheToolsBackIsLoggedByIdsAndCountsAndTheToolKeepsWorking()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: [fields["year"], fields["department"]]));
        await RagApi.ActivateAsync(setup.Admin, "rag__company");
        // What a deletion that raced with the save leaves behind.
        await ToolFixtures.ExecuteAsync(setup.App, "DELETE FROM knowledge_base.dictionary_fields WHERE \"Name\" = 'year'");
        setup.App.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("rag__company", JsonSerializer.Serialize(new { query = "leave" }), "call_1")),
            ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(setup.Admin, await ChatApi.CreateConversationAsync(setup.Admin), "Question");

        var offered = setup.App.ModelServer.ChatRequests[0].GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "rag__company")
            .GetProperty("function").GetProperty("parameters").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("department", offered);
        Assert.DoesNotContain("year_min", offered);
        Assert.Equal("succeeded", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains(setup.App.Logs.Messages, m => m.Contains("names 1 dictionary fields that no longer exist", StringComparison.Ordinal));
    }
}
