using System.Net;
using System.Text.Json;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Integration.Tests.WebSearch;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Rag;
using Quorum.Modules.Agents.Tools.WebFetch;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.WebFetch;

/// <summary>
/// How much of a page the model is given (spec 006, AC-6, AC-7): a page longer than the tool's budget is cut and the model is told,
/// pages that cannot all fit are dropped in the order they were asked, and the whole result stays inside the size the tenant allows.
/// </summary>
public sealed class WebFetchToolLimitsTests(PostgresFixture postgres)
{
    private const string PageOne = "https://site.test/one";
    private const string PageTwo = "https://site.test/two";
    private const string PageThree = "https://site.test/three";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeToolCall Call(params string[] urls) =>
        new(ToolNaming.WebFetch("read"), JsonSerializer.Serialize(new { urls }), "call_1");

    private static string ResultOf(IEnumerable<SseEvent> events) =>
        events.Single(e => e.Type == "tool_result").Data.GetProperty("content").GetString()!;

    /// <summary>A page whose main text is <paramref name="characters"/> long, with a title the citation repeats.</summary>
    private static string Page(string title, int characters) =>
        $"<html><head><title>{title}</title></head><body><article><p>{new string('s', characters)}</p></article></body></html>";

    private static WebFetchResponse Ok(string url, int characters) =>
        WebFetchResponse.Ok("text/html", url.Split('/')[^1], new string('c', characters), DateTimeOffset.UtcNow);

    [Fact] // AC-6
    public void APageLongerThanThePerToolBudgetIsCutAndTheModelIsTold()
    {
        var output = WebFetchResultFormat.Render(
            [PageOne],
            [Ok(PageOne, 30_000)],
            new SourceNumbering(),
            tokenBudgetPerPage: 500,
            totalTokenBudget: 20_000,
            resultLimit: 20_000);

        // 500 tokens is 2000 characters: the page keeps its reference and loses the rest of it.
        Assert.True(output.Text.Length < 3_000, $"the text is {output.Text.Length} characters");
        Assert.Contains("1 page was shortened to fit the tool's token budget", output.Text, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(output.Sources!).N);
    }

    [Fact] // AC-7
    public void PagesThatDoNotFitTheTotalBudgetTogetherAreDroppedInOrder()
    {
        var output = WebFetchResultFormat.Render(
            [PageOne, PageTwo, PageThree],
            [Ok(PageOne, 10_000), Ok(PageTwo, 10_000), Ok(PageThree, 10_000)],
            new SourceNumbering(),
            tokenBudgetPerPage: 4_000,
            totalTokenBudget: 2_000,
            resultLimit: 20_000);

        // 2000 tokens is 8000 characters for the whole call: the first page takes them, the other two get nothing.
        Assert.Contains("[1] one", output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[2]", output.Text, StringComparison.Ordinal);
        Assert.Contains("1 page was shortened", output.Text, StringComparison.Ordinal);
        Assert.Contains("2 pages were not read", output.Text, StringComparison.Ordinal);
        Assert.Single(output.Sources!);
    }

    [Fact] // spec 006, edge 4
    public void TheWholeResultStaysInsideTheSizeTheTenantAllowsForOneToolAnswer()
    {
        var output = WebFetchResultFormat.Render(
            [PageOne, PageTwo, PageThree],
            [Ok(PageOne, 30_000), Ok(PageTwo, 30_000), Ok(PageThree, 30_000)],
            new SourceNumbering(),
            tokenBudgetPerPage: 4_000,
            totalTokenBudget: 20_000,
            resultLimit: 20_000);

        // The invoker throws away a result larger than the tenant's cap and tells the model to narrow arguments it never chose,
        // so the renderer has to stop at the cap itself: 4000 tokens a page is 16000 characters, well past 20000 in three pages.
        Assert.True(output.Text.Length <= 20_000, $"the text is {output.Text.Length} characters");
        Assert.NotEmpty(output.Sources!);
        Assert.Contains("not read", output.Text, StringComparison.Ordinal);
    }

    [Fact] // spec 006, edge 3
    public async Task TheToolCutsAPageToTheBudgetItsAdministratorGaveItAndNotThePlatformDefault()
    {
        var (app, admin, chat) = await StartAsync(
            tool: WebFetchApi.Body(name: "read", tokenBudgetPerPage: 500),
            script: s => s.ServeHtml(PageOne, Page("Report", 9_000)));
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call(PageOne)), ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(admin, chat, "Read the long page");

        var result = ResultOf(events);
        Assert.Equal("succeeded", events.Single(e => e.Type == "tool_result").Data.GetProperty("status").GetString());

        // The platform's default of 4000 tokens would have given the model all 9000 characters of the article.
        Assert.Contains("1 page was shortened to fit the tool's token budget", result, StringComparison.Ordinal);
        Assert.True(result.Length < 3_000, $"the model was given {result.Length} characters");
    }

    [Fact] // spec 006, edge 4
    public async Task TheToolKeepsThePagesToTheTotalBudgetItsAdministratorGaveIt()
    {
        var (app, admin, chat) = await StartAsync(
            tool: WebFetchApi.Body(name: "read", tokenBudgetPerPage: 4_000, totalTokenBudget: 2_000),
            script: s =>
            {
                s.ServeHtml(PageOne, Page("One", 7_000));
                s.ServeHtml(PageTwo, Page("Two", 7_000));
            });
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call(PageOne, PageTwo)), ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(admin, chat, "Read both");

        var result = ResultOf(events);

        // 2000 tokens is 8000 characters for the whole call: the first page keeps its text, the second only gets what is left.
        Assert.Contains("[1] One", result, StringComparison.Ordinal);
        Assert.Contains("[2] Two", result, StringComparison.Ordinal);
        Assert.Contains("1 page was shortened to fit the tool's token budget", result, StringComparison.Ordinal);
        Assert.True(result.Length < 9_000, $"the model was given {result.Length} characters");
    }

    [Fact] // AC-1, AC-13
    public async Task AToolCreatedWithoutOverridesIsGivenWhatThePlatformRowSaysToday()
    {
        var (app, admin, chat) = await StartAsync(
            WebSearchApi.DefaultConfiguration with { FetchDelayMs = 0, FetchTokenBudgetPerPage = 500 },
            tool: WebFetchApi.Body(name: "read"),
            script: s => s.ServeHtml(PageOne, Page("Report", 9_000)));
        app.ModelServer.EnqueueRounds(ChatScript.CallTools(Call(PageOne)), ChatScript.Reply("Done."));

        var events = await ChatApi.SendAsync(admin, chat, "Read the long page");

        Assert.Contains("1 page was shortened to fit the tool's token budget", ResultOf(events), StringComparison.Ordinal);
    }

    /// <summary>An application with the platform's web row set, one web fetch tool activated, and a conversation to ask in.</summary>
    private async Task<(QuorumApp App, HttpClient Admin, Guid Chat)> StartAsync(
        WebSearchApi.WebConfigurationBody? web = null,
        object? tool = null,
        Action<FakeWebFetchServer>? script = null)
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        using (var configured = await WebFetchApi.UpdateConfigurationAsync(
                   admin, web ?? WebSearchApi.DefaultConfiguration with { FetchDelayMs = 0 }))
        {
            configured.EnsureSuccessStatusCode();
        }

        await WebFetchApi.CreateOkAsync(admin, tool ?? WebFetchApi.Body(name: "read"));
        await ToolFixtures.ActivateOnlyAsync(admin, "web_fetch__read");
        script?.Invoke(app.WebFetch);
        return (app, admin, await ChatApi.CreateConversationAsync(admin));
    }
}
