using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Rag;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Rag;

/// <summary>The size of a rag result against the tenant's limit on tool results, and what is told when the definition of a tool names a field that is gone.</summary>
public sealed class RagToolResultLimitTests(SharedQuorumApp host, QdrantFixture qdrant) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AResultSizeThatCannotFitTheTenantsLimitOnToolResultsIsRejectedAtSaveAndAcceptedOnceTheLimitIsRaised()
    {
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);
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
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);
        var kb = await setup.RegisterAsync("policies");
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], topK: 20, maxResultCharacters: 12_000));
        await RagApi.ActivateAsync(setup.Admin, "company");
        // Lowered afterwards: twenty sources no longer fit with their formatting, so the search asks for less.
        await ToolFixtures.SetLimitsAsync(setup.Admin, maxResult: 6_700);
        setup.App.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("company", JsonSerializer.Serialize(new { query = "policy" }), "call_1")),
            ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(setup.Admin, await ChatApi.CreateConversationAsync(setup.Admin), "Question");

        var result = Assert.Single(events, e => e.Type == "tool_result").Data;
        Assert.Equal("succeeded", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("content").GetString()!.Length <= 6_700);
    }


    private static bool OffersCompany(JsonElement request) =>
        request.TryGetProperty("tools", out var tools)
        && tools.EnumerateArray().Any(tool => tool.GetProperty("function").GetProperty("name").GetString() == "company");

    [Fact]
    public async Task ADeletedKnowledgeBaseLeavesTheToolsOfTheNextTurnEvenThoughTheGraphIsKept()
    {
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);
        var kb = await setup.RegisterAsync("policies");
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));
        await RagApi.ActivateAsync(setup.Admin, "company");

        await ChatApi.SendAsync(setup.Admin, await ChatApi.CreateConversationAsync(setup.Admin), "First");
        Assert.True(OffersCompany(setup.App.ModelServer.ChatRequests[^1]));

        using var delete = await setup.Admin.DeleteAsync(KnowledgeBaseApi.One(kb), Ct);
        delete.EnsureSuccessStatusCode();
        await ChatApi.SendAsync(setup.Admin, await ChatApi.CreateConversationAsync(setup.Admin), "Second");

        Assert.False(OffersCompany(setup.App.ModelServer.ChatRequests[^1]));
    }

    [Fact]
    public async Task AWriteTheKnowledgeBaseModuleMakesWithoutASaveStillChangesItsStamp()
    {
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);
        await using var scope = TenantScope.Open(setup.App, setup.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var stamp = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseChangeStamp>();

        var start = stamp.Current;
        _ = await db.Database.ExecuteSqlAsync($"SELECT 1", Ct);
        Assert.Equal(start, stamp.Current);

        _ = await db.Database.ExecuteSqlAsync($"UPDATE knowledge_base.dictionary_fields SET \"Name\" = \"Name\" WHERE false", Ct);
        Assert.True(stamp.Current > start);
    }
    [Fact]
    public async Task AFieldDeletedBehindTheToolsBackIsLoggedByIdsAndCountsAndTheToolKeepsWorking()
    {
        await using var setup = await RagApi.Setup.OverAsync(host, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: [fields["year"], fields["department"]]));
        await RagApi.ActivateAsync(setup.Admin, "company");
        // What a deletion that raced with the save leaves behind.
        await ToolFixtures.ExecuteAsync(setup.App, "DELETE FROM knowledge_base.dictionary_fields WHERE \"Name\" = 'year'");
        setup.App.ModelServer.EnqueueRounds(
            ChatScript.CallTools(new FakeToolCall("company", JsonSerializer.Serialize(new { query = "leave" }), "call_1")),
            ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(setup.Admin, await ChatApi.CreateConversationAsync(setup.Admin), "Question");

        var offered = setup.App.ModelServer.ChatRequests[0].GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("function").GetProperty("name").GetString() == "company")
            .GetProperty("function").GetProperty("parameters").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("department", offered);
        Assert.DoesNotContain("year_min", offered);
        Assert.Equal("succeeded", Assert.Single(events, e => e.Type == "tool_result").Data.GetProperty("status").GetString());
        Assert.Contains(setup.App.Logs.Messages, m => m.Contains("names 1 dictionary fields that no longer exist", StringComparison.Ordinal));
    }
}
