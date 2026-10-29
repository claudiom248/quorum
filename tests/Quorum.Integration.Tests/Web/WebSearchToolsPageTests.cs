using System.Net;
using System.Text.Json;
using Bunit;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>The web search tools list and its drawer: what an administrator sees and saves (spec 005, AC-7, AC-10).</summary>
public sealed class WebSearchToolsPageTests : ListPageTestContext
{
    private static readonly Guid ToolId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5021");
    private static readonly Guid SharedTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5022");
    private static readonly Guid CreatedTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5023");

    private HttpStatusCode _createStatus = HttpStatusCode.Created;
    private string? _instanceUrl = "https://searxng.example.test";

    public WebSearchToolsPageTests()
    {
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/tools/web-search")
            {
                return Page(
                    Detail(ToolId, "research", shared: false),
                    new
                    {
                        id = SharedTool,
                        name = "partner",
                        description = "The partner's tool.",
                        scope = NoScopeIds("Global"),
                        state = "Enabled",
                        policy = "Auto",
                        shared = true,
                    });
            }

            if (method == HttpMethod.Get && path == "/api/settings/web")
            {
                // The platform's one web row (spec 006, Amendment 1 of spec 005): the page reads it to warn that no instance is set.
                return FakeApiHandler.Json(new
                {
                    searchUrl = _instanceUrl,
                    searchTimeoutSeconds = 90,
                    fetchTimeoutSeconds = 30,
                    fetchMaxUrlsPerInvocation = 5,
                    fetchTokenBudgetPerPage = 4000,
                    fetchTotalTokenBudget = 20000,
                    userAgent = "Quorum/1.0",
                    fetchDelayMs = 1000,
                    respectRobotsTxt = true,
                    cacheEnabled = true,
                    cacheTtlMinutes = 10,
                });
            }

            if (path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = Guid.NewGuid(), name = "Assistant" } });
            }

            if (path == "/api/tenants")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (method == HttpMethod.Get && path == $"/api/tools/web-search/{ToolId}")
            {
                return FakeApiHandler.Json(Detail(ToolId, "research", shared: false, withBinding: true));
            }

            if (method == HttpMethod.Get && path == $"/api/tools/web-search/{CreatedTool}")
            {
                return FakeApiHandler.Json(Detail(CreatedTool, "support", shared: false, withBinding: true));
            }

            if (method == HttpMethod.Post && path == "/api/tools/web-search")
            {
                return _createStatus == HttpStatusCode.Created
                    ? FakeApiHandler.Json(new { id = CreatedTool, definition = Definition("support") }, HttpStatusCode.Created)
                    : FakeApiHandler.Json(
                        new { title = "Bad request", detail = "Web search configuration missing: an administrator has to set the SearXNG instance of the platform first.", code = "web_search.not_configured" },
                        _createStatus);
            }

            if ((method == HttpMethod.Put && path.StartsWith("/api/tools/web-search/", StringComparison.Ordinal))
                || (method == HttpMethod.Delete && path.StartsWith("/api/tools/web-search/", StringComparison.Ordinal))
                || (method == HttpMethod.Put && path.StartsWith("/api/tools/", StringComparison.Ordinal)))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static object Definition(string name) => new
    {
        name = $"web_search__{name}",
        description = $"Searches the web for {name}.",
        inputSchema = Schema("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
        estimatedTokens = 60,
    };

    private static JsonElement Schema(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static object Detail(Guid id, string name, bool shared, bool withBinding = false) => withBinding
        ? new
        {
            id,
            name,
            description = "Searches the open web.",
            topN = 7,
            searchEngines = new[] { "google", "bing" },
            language = "it",
            safeSearch = 2,
            timeRange = "week",
            maxInvocationsPerConversation = 3,
            scope = NoScopeIds("Tenant"),
            state = "Enabled",
            policy = "Auto",
            definition = Definition(name),
        }
        : new
        {
            id,
            name,
            description = "Searches the open web for what is newest.",
            scope = NoScopeIds(shared ? "Global" : "Tenant"),
            state = "Enabled",
            policy = "Auto",
            shared,
        };

    [Fact] // AC-10
    public void TheToolsAreListedWithTheirNameDescriptionAndScope()
    {
        var page = Visit<WebSearchTools>("/tools/web-search");

        var row = page.Find("[data-web-search-tool=research]").ParentElement!.ParentElement!;
        Assert.Contains("web_search__research", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Searches the open web for what is newest.", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Tenant", row.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-10
    public void AToolOfAnotherTenantIsReadOnlyInTheList()
    {
        var page = Visit<WebSearchTools>("/tools/web-search");

        var row = page.Find("[data-web-search-tool=partner]").ParentElement!.ParentElement!;
        Assert.Contains("Shared with you", row.TextContent, StringComparison.Ordinal);
        Assert.Empty(row.QuerySelectorAll("select"));
    }

    [Fact] // AC-10
    public void EditingShowsTheSavedToolWithTheDefinitionTheModelGets()
    {
        var page = Visit<WebSearchTools>($"/tools/web-search/{ToolId}", p => p.Add(c => c.Id, ToolId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=web-search]")));

        Assert.Contains("web_search__research", page.Find("[data-definition=web_search__research]").TextContent, StringComparison.Ordinal);
        Assert.Equal("7", page.Find("input[data-web-search-top-n]").GetAttribute("value"));
        Assert.Equal("3", page.Find("input[data-web-search-invocations]").GetAttribute("value"));
        Assert.Contains("Delete", page.Find("[data-form=web-search]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-10
    public void SavingANewToolSendsWhatTheAdministratorTyped()
    {
        var page = Visit<WebSearchTools>("/tools/web-search/new", p => p.Add(c => c.Id, "new"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=web-search]")));

        var inputs = page.Find("[data-form=web-search]").QuerySelectorAll("input[type=text], textarea");
        inputs[0].Change("support");
        inputs[1].Change("Searches the web for support answers.");
        page.Find("input[data-web-search-top-n]").Change("9");
        page.Find("input[data-web-search-invocations]").Change("4");

        page.Find("[data-form=web-search] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Requests.Count(r => r.Method == HttpMethod.Post && r.Path == "/api/tools/web-search")));
        var body = Body(Api, HttpMethod.Post, "/api/tools/web-search");
        Assert.Equal("support", body.GetProperty("name").GetString());
        Assert.Equal(9, body.GetProperty("topN").GetInt32());
        Assert.Equal(4, body.GetProperty("maxInvocationsPerConversation").GetInt32());
        Assert.Equal("Tenant", body.GetProperty("scope").GetProperty("kind").GetString());
        page.WaitForAssertion(() => Assert.Contains("was saved", page.Markup, StringComparison.Ordinal));
    }

    [Fact] // AC-7
    public void AToolCannotBeSavedWhileThePlatformHasNoInstanceAndThePageSaysWhy()
    {
        _createStatus = HttpStatusCode.BadRequest;
        _instanceUrl = null;
        var page = Visit<WebSearchTools>("/tools/web-search/new", p => p.Add(c => c.Id, "new"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=web-search]")));
        Assert.Contains("no search instance yet", page.Markup, StringComparison.Ordinal);

        page.Find("[data-form=web-search] form").Submit();

        page.WaitForAssertion(() => Assert.Contains("Web search configuration missing", page.Markup, StringComparison.Ordinal));
    }

    [Fact] // AC-10
    public void DeletingAsksFirstAndThenRemovesTheTool()
    {
        var page = Visit<WebSearchTools>($"/tools/web-search/{ToolId}", p => p.Add(c => c.Id, ToolId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=web-search]")));

        page.FindAll("[data-form=web-search] button").First(b => b.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();
        Assert.Equal(0, Api.Count(HttpMethod.Delete, "/api/tools/web-search"));
        Dialogs.WaitForAssertion(() => Assert.Contains("removed from every assistant", Dialogs.Markup, StringComparison.Ordinal));
        ClickButton(Dialogs, "Delete tool");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Delete, $"/api/tools/web-search/{ToolId}")));
    }

    [Fact] // AC-10
    public void TheStateAndThePolicyAreChangedInTheList()
    {
        var page = Visit<WebSearchTools>("/tools/web-search");

        page.Find("[data-web-search-tool=research]").ParentElement!.ParentElement!.QuerySelector("select")!.Change(Quorum.Web.Client.Api.ToolPolicies.Confirm);

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/tools/{ToolId}")));
        var body = Body(Api, HttpMethod.Put, $"/api/tools/{ToolId}");
        Assert.Equal(("Enabled", "Confirm"), (body.GetProperty("state").GetString(), body.GetProperty("policy").GetString()));
    }
}
