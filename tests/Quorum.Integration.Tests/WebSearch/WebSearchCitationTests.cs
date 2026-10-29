using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Rag;
using Quorum.Integration.Tests.Tools;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.WebSearch;

/// <summary>
/// What a search of the web leaves behind in the answer: the sources the user can open, and the two search tools side by side so the
/// prompt can say which one to try first (spec 005, AC-6, AC-8).
/// </summary>
public sealed class WebSearchCitationTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeToolCall Call(string query, string id = "call_1") =>
        new("web_search__research", JsonSerializer.Serialize(new { query }), id);

    private static JsonElement[] SourcesOf(SseEvent sourcesEvent) => [.. sourcesEvent.Data.GetProperty("sources").EnumerateArray()];

    private static string ResultOf(IEnumerable<SseEvent> events, int index = 0) =>
        events.Where(e => e.Type == "tool_result").ElementAt(index).Data.GetProperty("content").GetString()!;

    private static async Task<JsonElement[]> StoredAsync(HttpClient client, Guid conversation, int messageIndex)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(ChatApi.Conversation(conversation), Ct);
        return [.. body.GetProperty("messages")[messageIndex].GetProperty("sources").EnumerateArray()];
    }

    /// <summary>An application with the instance set, one web search tool activated, and a conversation to ask in.</summary>
    private async Task<(QuorumApp App, HttpClient Admin, Guid Chat)> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research"));
        await ToolFixtures.ActivateOnlyAsync(admin, "web_search__research");
        app.Searxng.Answers(
            ("AI News", "https://example.com/ai-news", "The newest of the new."),
            ("Second", "https://other.test/report", "Another extract."));
        return (app, admin, await ChatApi.CreateConversationAsync(admin));
    }

    [Fact] // AC-6
    public async Task AWebSourceIsPublishedWithTheAnswerStoredWithItAndSaysWhereItCameFrom()
    {
        var (app, admin, chat) = await StartAsync();
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call("ai news")), ChatScript.Reply("The news is [1], and the rest is [2]."));

        var events = await ChatApi.SendAsync(admin, chat, "What is new?");
        var published = SourcesOf(events.Single(e => e.Type == "sources"));
        var stored = await StoredAsync(admin, chat, 1);

        Assert.Equal([1, 2], published.Select(s => s.GetProperty("n").GetInt32()));
        Assert.Equal("web", published[0].GetProperty("knowledgeBase").GetString());
        Assert.Equal("AI News", published[0].GetProperty("title").GetString());
        Assert.Equal("https://example.com/ai-news", published[0].GetProperty("url").GetString());
        Assert.True(string.IsNullOrEmpty(published[0].GetProperty("documentId").GetString()));
        Assert.True(string.IsNullOrEmpty(published[0].GetProperty("position").GetString()));
        Assert.Equal(published.Select(s => s.GetRawText()), stored.Select(s => s.GetRawText()));
        // What the page says is not stored with the citation, only where it is.
        Assert.DoesNotContain("The newest of the new", events.Single(e => e.Type == "sources").Data.GetRawText(), StringComparison.Ordinal);
    }

    [Fact] // AC-8
    public async Task AKnowledgeBaseToolAndAWebSearchToolAreOfferedTogetherSoThePromptCanChoose()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var knowledgeBase = await setup.RegisterAsync("policies");
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [knowledgeBase]));
        await WebSearchApi.ConfigureAsync(setup.Admin);
        await WebSearchApi.CreateOkAsync(setup.Admin, WebSearchApi.Body(name: "research"));
        await ToolFixtures.ActivateOnlyAsync(setup.Admin, "rag__company", "web_search__research");
        setup.App.ModelServer.EnqueueRounds(ChatScript.Reply("Answered from both."));

        await ChatApi.SendAsync(setup.Admin, await ChatApi.CreateConversationAsync(setup.Admin), "Is this in the policies or on the web?");

        var request = setup.App.ModelServer.ChatRequests[0];
        string[] offered = [.. request.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function").GetProperty("name").GetString()!)];
        Assert.Contains("rag__company", offered);
        Assert.Contains("web_search__research", offered);

        // Both kinds of source carry the same numbers, so the answer can cite them in one list.
        var system = request.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;
        Assert.Contains("a knowledge base or a web search", system, StringComparison.Ordinal);
    }

    [Fact] // AC-6
    public async Task TheSamePageFoundTwiceKeepsOneNumberSoTheAnswerCitesItOnce()
    {
        var (app, admin, chat) = await StartAsync();
        app.Searxng.Answers(("AI News", "https://example.com/ai-news", "The newest of the new."));
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Call("first", "call_1")),
            ChatScript.CallTools(Call("again", "call_2")),
            ChatScript.Reply("Still [1]."));

        var events = await ChatApi.SendAsync(admin, chat, "Ask twice");

        // Both searches cite one source, published once: a page the answer already has keeps its number and is not listed again.
        var published = events.Where(e => e.Type == "sources").Select(SourcesOf).ToList();
        var stored = await StoredAsync(admin, chat, 1);
        Assert.Single(published);
        Assert.Equal(1, published[0][0].GetProperty("n").GetInt32());
        Assert.Equal([1], stored.Select(s => s.GetProperty("n").GetInt32()).ToArray());
        Assert.Contains("[1] AI News", ResultOf(events, 1), StringComparison.Ordinal);
    }
}
