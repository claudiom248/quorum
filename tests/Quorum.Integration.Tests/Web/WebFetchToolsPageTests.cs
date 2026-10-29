using System.Net;
using System.Text.Json;
using Bunit;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>The web fetch tools list and its drawer: what an administrator sees and saves (spec 006, AC-15).</summary>
public sealed class WebFetchToolsPageTests : ListPageTestContext
{
    private static readonly Guid ToolId = Guid.Parse("01a14797-6817-7e25-b071-1da93d0b5031");
    private static readonly Guid SharedTool = Guid.Parse("01a14797-6817-7e25-b071-1da93d0b5032");
    private static readonly Guid CreatedTool = Guid.Parse("01a14797-6817-7e25-b071-1da93d0b5033");

    public WebFetchToolsPageTests()
    {
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/tools/web-fetch")
            {
                return Page(
                    Detail(ToolId, "reader", shared: false),
                    new
                    {
                        id = SharedTool,
                        name = "partner",
                        description = "The partner's reader.",
                        scope = NoScopeIds("Global"),
                        state = "Enabled",
                        policy = "Auto",
                        shared = true,
                    });
            }

            if (path == "/api/settings/web")
            {
                return method == HttpMethod.Get
                    ? FakeApiHandler.Json(new
                    {
                        searchUrl = "https://searxng.example.test",
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
                    })
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = Guid.NewGuid(), name = "Assistant" } });
            }

            if (path == "/api/tenants")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (method == HttpMethod.Get && path == $"/api/tools/web-fetch/{ToolId}")
            {
                return FakeApiHandler.Json(Detail(ToolId, "reader", shared: false, withBinding: true));
            }

            if (method == HttpMethod.Get && path == $"/api/tools/web-fetch/{CreatedTool}")
            {
                return FakeApiHandler.Json(Detail(CreatedTool, "docs", shared: false, withBinding: true));
            }

            if (method == HttpMethod.Post && path == "/api/tools/web-fetch")
            {
                return FakeApiHandler.Json(new { id = CreatedTool, definition = Definition("docs") }, HttpStatusCode.Created);
            }

            if ((method == HttpMethod.Put && path.StartsWith("/api/tools/web-fetch/", StringComparison.Ordinal))
                || (method == HttpMethod.Delete && path.StartsWith("/api/tools/web-fetch/", StringComparison.Ordinal))
                || (method == HttpMethod.Put && path.StartsWith("/api/tools/", StringComparison.Ordinal)))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static object Definition(string name) => new
    {
        name = $"web_fetch__{name}",
        description = $"Reads the pages {name} is asked for.",
        inputSchema = Schema("""{"type":"object","properties":{"urls":{"type":"array","items":{"type":"string"},"maxItems":5}},"required":["urls"]}"""),
        estimatedTokens = 70,
    };

    private static JsonElement Schema(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <param name="withBinding">The detail of one tool, which carries its overrides; a field left out of the row is inheritance.</param>
    private static object Detail(Guid id, string name, bool shared, bool withBinding = false) => withBinding
        ? new
        {
            id,
            name,
            description = "Reads the pages the model names.",
            tokenBudgetPerPage = 1000,
            totalTokenBudget = (int?)null,
            maxUrlsPerInvocation = (int?)null,
            timeout = (int?)null,
            scope = NoScopeIds("Tenant"),
            state = "Enabled",
            policy = "Auto",
            definition = Definition(name),
        }
        : new
        {
            id,
            name,
            description = "Reads the pages the model names.",
            scope = NoScopeIds(shared ? "Global" : "Tenant"),
            state = "Enabled",
            policy = "Auto",
            shared,
        };

    [Fact] // AC-15
    public void TheToolsAreListedWithTheirNameDescriptionAndScope()
    {
        var page = Visit<WebTools>("/tools/web/fetch", p => p.Add(c => c.Section, "fetch"));

        var row = page.Find("[data-web-fetch-tool=reader]").ParentElement!.ParentElement!;
        Assert.Contains("web_fetch__reader", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Reads the pages the model names.", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Tenant", row.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-15
    public void AToolOfAnotherTenantIsReadOnlyInTheList()
    {
        var page = Visit<WebTools>("/tools/web/fetch", p => p.Add(c => c.Section, "fetch"));

        var row = page.Find("[data-web-fetch-tool=partner]").ParentElement!.ParentElement!;
        Assert.Contains("Shared with you", row.TextContent, StringComparison.Ordinal);
        Assert.Empty(row.QuerySelectorAll("select"));
    }

    [Fact] // AC-15, AC-13
    public void TheDrawerShowsTheOverrideAndLeavesAnInheritedFieldEmpty()
    {
        var page = Visit<WebTools>($"/tools/web/fetch/{ToolId}", p =>
        {
            p.Add(c => c.Section, "fetch");
            p.Add(c => c.Id, ToolId.ToString());
        });
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=web-fetch]")));

        // What the tool decided for itself is shown; what it leaves to the platform is an empty field, not the platform's number.
        Assert.Equal("1000", page.Find("input[data-web-fetch-per-page]").GetAttribute("value"));
        AssertFieldEmpty(page, "data-web-fetch-total");
        AssertFieldEmpty(page, "data-web-fetch-urls");
        AssertFieldEmpty(page, "data-web-fetch-timeout");
        Assert.Contains("4000 tokens a page", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Delete", page.Find("[data-form=web-fetch]").TextContent, StringComparison.Ordinal);
    }

    private static void AssertFieldEmpty(IRenderedComponent<WebTools> page, string attribute)
    {
        var value = page.Find($"input[{attribute}]").GetAttribute("value");
        Assert.True(string.IsNullOrEmpty(value), $"the field carries {value}");
    }

    [Fact] // AC-15, AC-1
    public void SavingAFormWithNothingInItsBudgetFieldsAsksThePlatformToDecide()
    {
        var page = Visit<WebTools>("/tools/web/fetch/new", p =>
        {
            p.Add(c => c.Section, "fetch");
            p.Add(c => c.Id, "new");
        });
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=web-fetch]")));

        page.Find("input[data-web-fetch-urls]").Change("3");
        page.Find("[data-form=web-fetch] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Post, "/api/tools/web-fetch")));
        var body = Body(Api, HttpMethod.Post, "/api/tools/web-fetch");
        Assert.Equal(3, body.GetProperty("maxUrlsPerInvocation").GetInt32());

        // An empty field goes on as null: the tool inherits what the platform says, now and when it changes.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("tokenBudgetPerPage").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("totalTokenBudget").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("timeout").ValueKind);
    }

    [Fact] // AC-15
    public void AToolCannotBeSavedWithoutANameAndThePageSaysWhy()
    {
        var page = Visit<WebTools>("/tools/web/fetch/new", p =>
        {
            p.Add(c => c.Section, "fetch");
            p.Add(c => c.Id, "new");
        });
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=web-fetch]")));

        page.Find("[data-form=web-fetch] input[type=text]").Change(string.Empty);
        page.Find("[data-form=web-fetch] form").Submit();

        page.WaitForAssertion(() => Assert.Contains("Give the tool a name", page.Markup, StringComparison.Ordinal));
        Assert.Equal(0, Api.Count(HttpMethod.Post, "/api/tools/web-fetch"));
    }

    [Fact] // AC-15
    public void TheNumberTheModelIsGivenComesFromTheSavedTool()
    {
        var page = Visit<WebTools>($"/tools/web/fetch/{ToolId}", p =>
        {
            p.Add(c => c.Section, "fetch");
            p.Add(c => c.Id, ToolId.ToString());
        });
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=web-fetch]")));

        Assert.NotNull(page.Find("[data-definition=web_fetch__reader]"));
        Assert.Contains("web_fetch__reader", page.Find("[data-definition=web_fetch__reader]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-15
    public void DeletingAsksFirstAndThenRemovesTheTool()
    {
        var page = Visit<WebTools>($"/tools/web/fetch/{ToolId}", p =>
        {
            p.Add(c => c.Section, "fetch");
            p.Add(c => c.Id, ToolId.ToString());
        });
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=web-fetch]")));

        page.FindAll("[data-form=web-fetch] button").First(b => b.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();
        Assert.Equal(0, Api.Count(HttpMethod.Delete, "/api/tools/web-fetch"));
        Dialogs.WaitForAssertion(() => Assert.Contains("removed from every assistant", Dialogs.Markup, StringComparison.Ordinal));
        ClickButton(Dialogs, "Delete tool");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Delete, $"/api/tools/web-fetch/{ToolId}")));
    }

    [Fact] // AC-15
    public void TheStateAndThePolicyAreChangedInTheList()
    {
        var page = Visit<WebTools>("/tools/web/fetch", p => p.Add(c => c.Section, "fetch"));

        page.Find("[data-web-fetch-tool=reader]").ParentElement!.ParentElement!.QuerySelector("select")!.Change(Quorum.Web.Client.Api.ToolPolicies.Confirm);

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/tools/{ToolId}")));
        var body = Body(Api, HttpMethod.Put, $"/api/tools/{ToolId}");
        Assert.Equal(("Enabled", "Confirm"), (body.GetProperty("state").GetString(), body.GetProperty("policy").GetString()));
    }
}
