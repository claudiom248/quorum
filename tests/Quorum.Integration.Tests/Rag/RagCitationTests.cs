using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.Tools;
using Quorum.SampleKnowledgeBase;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Rag;

/// <summary>The sources of an answer: streamed as they are found, stored with the message and returned when the conversation is reopened (spec 003, AC-37 to AC-41).</summary>
public sealed class RagCitationTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(ulong chunk) => SampleCollections.CreateFlat().Points.Single(p => p.Id == chunk).EmbeddingText;

    private static FakeToolCall Call(object arguments, string id = "call_1") =>
        new("rag__company", JsonSerializer.Serialize(arguments), id);

    /// <summary>One knowledge base called <c>policies</c> and a rag tool over it, activated on the assistant.</summary>
    private async Task<RagApi.Setup> StartAsync(int topK = 2, double? scoreThreshold = null)
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], topK: topK, scoreThreshold: scoreThreshold));
        await RagApi.ActivateAsync(setup.Admin, "rag__company");
        return setup;
    }

    private static async Task<(Guid Conversation, IReadOnlyList<SseEvent> Events)> AskAsync(RagApi.Setup setup, params FakeToolCall[] calls)
    {
        setup.App.ModelServer.EnqueueRounds([.. calls.Select(c => ChatScript.CallTools(c)), ChatScript.Reply("Done [1].")]);
        var id = await ChatApi.CreateConversationAsync(setup.Admin);
        return (id, await ChatApi.SendAsync(setup.Admin, id, "Question"));
    }

    private static async Task<IReadOnlyList<SseEvent>> RegenerateAsync(HttpClient client, Guid conversation)
    {
        using var response = await client.PostAsync(ChatApi.Regenerate(conversation), content: null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SseReader.ReadAllAsync(response);
    }

    private static JsonElement[] SourcesOf(SseEvent sourcesEvent) => [.. sourcesEvent.Data.GetProperty("sources").EnumerateArray()];

    private static async Task<JsonElement[]> StoredAsync(HttpClient client, Guid conversation, int messageIndex)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(ChatApi.Conversation(conversation), Ct);
        return [.. body.GetProperty("messages")[messageIndex].GetProperty("sources").EnumerateArray()];
    }

    [Fact] // AC-37
    public async Task AToolCallThatReturnsSourcesPublishesThemBeforeItsResult()
    {
        await using var setup = await StartAsync();

        var (_, events) = await AskAsync(setup, Call(new { query = Text(1) }));

        var types = events.Select(e => e.Type).ToList();
        var sources = events.Single(e => e.Type == "sources");
        Assert.Equal(["tool_call", "sources", "tool_result"], types.Where(t => t is "tool_call" or "sources" or "tool_result"));
        var cited = SourcesOf(sources);
        Assert.Equal([1, 2], cited.Select(s => s.GetProperty("n").GetInt32()));
        var best = cited[0];
        Assert.Equal("policies", best.GetProperty("knowledgeBase").GetString());
        Assert.Equal("Annual leave policy", best.GetProperty("title").GetString());
        Assert.Equal("1", best.GetProperty("position").GetString());
        Assert.False(string.IsNullOrEmpty(best.GetProperty("documentId").GetString()));
        Assert.StartsWith("https://intranet.example.test/", best.GetProperty("url").GetString(), StringComparison.Ordinal);
        // References only: the text of the passage reaches the model and the tool_result, never this event.
        Assert.DoesNotContain("Employees accrue", sources.Data.GetRawText(), StringComparison.Ordinal);
    }

    [Fact] // AC-37
    public async Task TheNumbersContinueAcrossTheCallsOfOneAnswer()
    {
        await using var setup = await StartAsync();

        var (_, events) = await AskAsync(setup, Call(new { query = Text(1) }, "call_1"), Call(new { query = Text(6) }, "call_2"));

        var published = events.Where(e => e.Type == "sources").Select(SourcesOf).ToList();
        Assert.Equal([[1, 2], [3, 4]], published.Select(s => s.Select(x => x.GetProperty("n").GetInt32()).ToArray()));
        Assert.Equal("Travel expense rules", published[1][0].GetProperty("title").GetString());
        // Each call's sources come before that call's own result.
        var order = events.Where(e => e.Type is "sources" or "tool_result").Select(e => e.Type).ToList();
        Assert.Equal(["sources", "tool_result", "sources", "tool_result"], order);
    }

    [Fact] // AC-37
    public async Task ASourceFoundAgainKeepsItsNumberAndIsPublishedAndStoredOnce()
    {
        await using var setup = await StartAsync();

        var (conversation, events) = await AskAsync(setup, Call(new { query = Text(1) }, "call_1"), Call(new { query = Text(1) }, "call_2"));

        // The second search returns the same two passages: the model reads them as [1] and [2] again, and nothing new is published.
        Assert.Single(events, e => e.Type == "sources");
        var results = events.Where(e => e.Type == "tool_result").ToList();
        var second = results[1].Data.GetProperty("content").GetString()!;
        Assert.Contains("[1] policies", second, StringComparison.Ordinal);
        Assert.Contains("[2] policies", second, StringComparison.Ordinal);
        Assert.DoesNotContain("[3]", second, StringComparison.Ordinal);
        var stored = await StoredAsync(setup.Admin, conversation, 1);
        Assert.Equal([1, 2], stored.Select(s => s.GetProperty("n").GetInt32()));
    }

    [Fact] // AC-37
    public async Task ACallWithNothingFoundOrThatFailedPublishesNoSources()
    {
        await using var setup = await StartAsync(scoreThreshold: 2.0);

        var (_, nothing) = await AskAsync(setup, Call(new { query = Text(1) }));
        var (_, invalid) = await AskAsync(setup, Call(new { query = "policy", unknown_filter = 1 }, "call_2"));

        Assert.DoesNotContain(nothing, e => e.Type == "sources");
        Assert.DoesNotContain(invalid, e => e.Type == "sources");
    }

    [Fact] // AC-38
    public async Task ReopeningTheConversationReturnsTheSourcesOfEachAnswerAsReferencesOnly()
    {
        await using var setup = await StartAsync();
        var (id, events) = await AskAsync(setup, Call(new { query = Text(1) }, "call_1"), Call(new { query = Text(6) }, "call_2"));

        var conversation = await setup.Admin.GetFromJsonAsync<JsonElement>(ChatApi.Conversation(id), Ct);

        var messages = conversation.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["User", "Assistant"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Empty(messages[0].GetProperty("sources").EnumerateArray());
        var stored = messages[1].GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3, 4], stored.Select(s => s.GetProperty("n").GetInt32()));
        var streamed = events.Where(e => e.Type == "sources").SelectMany(SourcesOf).ToList();
        Assert.Equal(streamed.Select(s => s.GetRawText()), stored.Select(s => s.GetRawText()));
        Assert.DoesNotContain("Employees accrue", messages[1].GetProperty("sources").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerWithoutSourcesReturnsAnEmptyList()
    {
        await using var setup = await StartAsync();
        setup.App.ModelServer.EnqueueRounds(ChatScript.Reply("Hello."));
        var id = await ChatApi.CreateConversationAsync(setup.Admin);
        await ChatApi.SendAsync(setup.Admin, id, "Hi");

        Assert.Empty(await StoredAsync(setup.Admin, id, 1));
    }

    [Fact] // AC-39
    public async Task ARegenerationReplacesTheSourcesOfTheMessage()
    {
        await using var setup = await StartAsync();
        var (id, _) = await AskAsync(setup, Call(new { query = Text(1) }));
        var before = await StoredAsync(setup.Admin, id, 1);
        setup.App.ModelServer.EnqueueRounds(ChatScript.CallTools(Call(new { query = Text(6) })), ChatScript.Reply("Done again."));

        var events = await RegenerateAsync(setup.Admin, id);

        Assert.Equal("Annual leave policy", before[0].GetProperty("title").GetString());
        // Numbering starts again with the new answer.
        var after = await StoredAsync(setup.Admin, id, 1);
        Assert.Equal([1, 2], after.Select(s => s.GetProperty("n").GetInt32()));
        Assert.Equal("Travel expense rules", after[0].GetProperty("title").GetString());
        Assert.Equal(SourcesOf(events.Single(e => e.Type == "sources")).Select(s => s.GetRawText()), after.Select(s => s.GetRawText()));
        Assert.Equal(2, await ToolFixtures.ScalarAsync<long>(setup.App, "SELECT COUNT(*) FROM conversations.message_sources"));
    }

    [Fact] // AC-39
    public async Task ARegenerationThatCitesNothingLeavesNoSourcesOfTheReplacedAnswer()
    {
        await using var setup = await StartAsync();
        var (id, _) = await AskAsync(setup, Call(new { query = Text(1) }));
        setup.App.ModelServer.EnqueueRounds(ChatScript.Reply("From memory."));

        await RegenerateAsync(setup.Admin, id);

        Assert.Empty(await StoredAsync(setup.Admin, id, 1));
        Assert.Equal(0, await ToolFixtures.ScalarAsync<long>(setup.App, "SELECT COUNT(*) FROM conversations.message_sources"));
    }

    [Fact] // AC-39
    public async Task DeletingTheConversationDeletesItsSources()
    {
        await using var setup = await StartAsync();
        var (id, _) = await AskAsync(setup, Call(new { query = Text(1) }));
        var (other, _) = await AskAsync(setup, Call(new { query = Text(6) }));
        Assert.Equal(4, await ToolFixtures.ScalarAsync<long>(setup.App, "SELECT COUNT(*) FROM conversations.message_sources"));

        using var response = await setup.Admin.DeleteAsync(ChatApi.Conversation(id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, await ToolFixtures.ScalarAsync<long>(setup.App, "SELECT COUNT(*) FROM conversations.message_sources"));
        Assert.Equal(2, (await StoredAsync(setup.Admin, other, 1)).Length);
    }

    [Fact] // AC-41
    public async Task AKnowledgeBaseDeletedAfterTheAnswerLeavesTheStoredSourcesUntouched()
    {
        await using var setup = await StartAsync();
        var (id, _) = await AskAsync(setup, Call(new { query = Text(1) }));
        var before = await StoredAsync(setup.Admin, id, 1);

        using var delete = await setup.Admin.DeleteAsync(KnowledgeBaseApi.One(await ToolFixtures.ScalarAsync<Guid>(setup.App, "SELECT \"Id\" FROM knowledge_base.knowledge_bases LIMIT 1")), Ct);
        delete.EnsureSuccessStatusCode();

        var after = await StoredAsync(setup.Admin, id, 1);
        Assert.Equal(before.Select(s => s.GetRawText()), after.Select(s => s.GetRawText()));
        Assert.Equal("policies", after[0].GetProperty("knowledgeBase").GetString());
        Assert.Equal("Annual leave policy", after[0].GetProperty("title").GetString());
        Assert.StartsWith("https://intranet.example.test/", after[0].GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSourcesOfAConversationAreNotVisibleToAnotherUser()
    {
        await using var setup = await StartAsync();
        var (id, _) = await AskAsync(setup, Call(new { query = Text(1) }));
        var member = await setup.App.CreateMemberClientAsync();

        using var response = await member.GetAsync(ChatApi.Conversation(id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
