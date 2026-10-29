using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Domain;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.WebSearch;

/// <summary>What the model reads when the instance does not answer, and how often one conversation may ask it (spec 005, AC-3, AC-5).</summary>
public sealed class WebSearchToolLimitsTests(PostgresFixture postgres)
{
    private static FakeToolCall Call(string query, string id) =>
        new("web_search__research", JsonSerializer.Serialize(new { query }), id);

    private static string ResultOf(IEnumerable<SseEvent> events, int index = 0) =>
        events.Where(e => e.Type == "tool_result").ElementAt(index).Data.GetProperty("content").GetString()!;

    private static string StatusOf(IEnumerable<SseEvent> events, int index = 0) =>
        events.Where(e => e.Type == "tool_result").ElementAt(index).Data.GetProperty("status").GetString()!;

    [Fact] // AC-3
    public async Task AnInstanceThatNeverAnswersIsToldToTheModelAsUnavailable()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.UpdateConfigurationAsync(admin, WebSearchApi.DefaultConfiguration with { SearchUrl = "http://searxng.test", SearchTimeoutSeconds = 1 });
        await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research"));
        await ToolFixtures.ActivateOnlyAsync(admin, "web_search__research");
        app.Searxng.NeverAnswers();
        var chat = await ChatApi.CreateConversationAsync(admin);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call("anything", "call_1")), ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(admin, chat, "Hello");

        Assert.Equal("failed", StatusOf(events));
        Assert.Contains("Web search unavailable: SearXNG did not respond within 1 seconds", ResultOf(events), StringComparison.Ordinal);
        Assert.True(app.Searxng.Attempts >= 1);
    }

    [Fact] // AC-5
    public async Task OneConversationCannotAskTheToolMoreThanItsAdministratorAllowed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research", maxInvocationsPerConversation: 2));
        await ToolFixtures.ActivateOnlyAsync(admin, "web_search__research");
        app.Searxng.Answers(("One", "https://a.test/1", "first"));
        var chat = await ChatApi.CreateConversationAsync(admin);

        // Two calls are answered; the third in the same conversation is refused before the instance is asked again.
        app.ModelServer.EnqueueRounds(
            ChatScript.CallTools(Call("first", "call_1"), Call("second", "call_2"), Call("third", "call_3")),
            ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(admin, chat, "Search three times");

        Assert.Equal("succeeded", StatusOf(events, 0));
        Assert.Equal("succeeded", StatusOf(events, 1));
        Assert.Equal("failed", StatusOf(events, 2));
        Assert.Contains("Max invocations reached: 2", ResultOf(events, 2), StringComparison.Ordinal);
        Assert.Equal(2, app.Searxng.Attempts);
    }

    [Fact] // AC-5
    public async Task TheCountOfInvocationsIsPerConversationAndContinuesAcrossTheAnswersOfOneConversation()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research", maxInvocationsPerConversation: 2));
        await ToolFixtures.ActivateOnlyAsync(admin, "web_search__research");
        app.Searxng.Answers(("One", "https://a.test/1", "first"));
        var chat = await ChatApi.CreateConversationAsync(admin);
        var other = await ChatApi.CreateConversationAsync(admin);

        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call("one", "call_1")), ChatScript.Reply("First answer."));
        await ChatApi.SendAsync(admin, chat, "Search");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call("two", "call_2")), ChatScript.Reply("Second answer."));
        await ChatApi.SendAsync(admin, chat, "Search again");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call("three", "call_3")), ChatScript.Reply("Third answer."));
        var third = await ChatApi.SendAsync(admin, chat, "And again");
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call("fresh", "call_4")), ChatScript.Reply("Other chat."));
        var inAnotherConversation = await ChatApi.SendAsync(admin, other, "Search");

        Assert.Equal("succeeded", StatusOf(inAnotherConversation));
        Assert.Equal("failed", StatusOf(third));
        Assert.Contains("Max invocations reached: 2", ResultOf(third), StringComparison.Ordinal);
        Assert.Equal(3, app.Searxng.Attempts);
    }

    [Fact] // AC-5
    public async Task ACallTheUserRefusedDoesNotUseTheConversationsBudget()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await WebSearchApi.ConfigureAsync(admin);
        await WebSearchApi.CreateOkAsync(admin, WebSearchApi.Body(name: "research", maxInvocationsPerConversation: 1));
        await ToolFixtures.ActivateOnlyAsync(admin, "web_search__research");
        app.Searxng.Answers(("One", "https://a.test/1", "first"));
        var chat = await ChatApi.CreateConversationAsync(admin);

        // Two calls the user refused never reached the instance: the one call the tool allows is still there for the model.
        await ToolFixtures.AddAuditEntryAsync(app, DateTimeOffset.UtcNow, tool: "research", server: "web_search", outcome: ToolOutcome.Denied, conversationId: chat);
        await ToolFixtures.AddAuditEntryAsync(app, DateTimeOffset.UtcNow, tool: "research", server: "web_search", outcome: ToolOutcome.Denied, conversationId: chat);
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call("now", "call_1")), ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(admin, chat, "Search");

        Assert.Equal("succeeded", StatusOf(events));
        Assert.Equal(1, app.Searxng.Attempts);
    }
}
