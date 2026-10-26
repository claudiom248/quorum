using System.Net;
using Bunit;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>The MCP servers list and its drawer (spec 010, AC-3, AC-8, AC-9, AC-11, AC-16; spec 004 AC-48, D-14, D-25).</summary>
public sealed class McpServersPageTests : ListPageTestContext
{
    private static readonly Guid ServerId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1101");
    private static readonly Guid SharedId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1102");
    private static readonly Guid ReadTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1111");
    private static readonly Guid SearchTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1112");

    private static readonly string[] Added = ["list"];
    private static readonly string[] Changed = ["read"];

    private string _status = "Available";
    private string? _lastError;
    private bool _includeShared;
    private HttpStatusCode _listStatus = HttpStatusCode.OK;
    private string? _nextCursor;

    public McpServersPageTests()
    {
        Api.Respond = (method, path) =>
        {
            if (path == "/api/tools/servers" && method == HttpMethod.Get)
            {
                return _listStatus == HttpStatusCode.OK
                    ? Page(_nextCursor, Servers().Select(s => Server(s.Id, s.Name, s.Shared)).ToArray())
                    : new HttpResponseMessage(_listStatus);
            }

            if (path.EndsWith("/tools", StringComparison.Ordinal) && method == HttpMethod.Get)
            {
                return Page(
                    Tool(ReadTool, "read", "PendingReview", "Confirm", new { readOnlyHint = true }),
                    Tool(SearchTool, "search", "Enabled", "Auto", null));
            }

            if (method == HttpMethod.Get && path.StartsWith("/api/tools/servers/", StringComparison.Ordinal))
            {
                var server = Servers().SingleOrDefault(s => path.EndsWith(s.Id.ToString(), StringComparison.Ordinal));
                return server == default
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : FakeApiHandler.Json(Server(server.Id, server.Name, server.Shared));
            }

            if (path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = Guid.NewGuid(), name = "Assistant" } });
            }

            if (path == "/api/tenants")
            {
                return FakeApiHandler.Json(new[] { new { id = Guid.NewGuid(), name = "Acme", current = true } });
            }

            if (method == HttpMethod.Put && path.StartsWith("/api/tools/", StringComparison.Ordinal) && !path.StartsWith("/api/tools/servers/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Post && path.EndsWith("/refresh", StringComparison.Ordinal))
            {
                return FakeApiHandler.Json(new { status = "Available", added = Added, changed = Changed, removed = Array.Empty<string>() });
            }

            if (method == HttpMethod.Post && path == "/api/tools/servers/test")
            {
                return FakeApiHandler.Json(new { reachable = true, tools = new[] { new { name = "fetch", description = "Fetches.", annotations = new { readOnlyHint = true } } } });
            }

            if (method == HttpMethod.Post && path == "/api/tools/servers")
            {
                return FakeApiHandler.Json(new { id = Guid.NewGuid(), status = "Available", warning = (string?)null, tools = new[] { Tool(Guid.NewGuid(), "fetch", "PendingReview", "Confirm", null) } }, HttpStatusCode.Created);
            }

            if ((method == HttpMethod.Put || method == HttpMethod.Delete) && path.StartsWith("/api/tools/servers/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private (Guid Id, string Name, bool Shared)[] Servers() =>
        _includeShared ? [(ServerId, "files", false), (SharedId, "partner", true)] : [(ServerId, "files", false)];

    private static object Tool(Guid id, string name, string state, string policy, object? annotations) =>
        new { id, origin = "Mcp", serverName = "files", name, description = $"{name} does things.", state, policy, annotations };

    private object Server(Guid id, string name, bool shared) => new
    {
        id,
        name,
        url = $"http://{name}.test/mcp",
        hasHeaders = !shared,
        status = shared ? "Available" : _status,
        lastError = shared ? null : _lastError,
        lastRefreshedAt = (DateTimeOffset?)null,
        scope = NoScopeIds(shared ? "Global" : "Tenant"),
        toolCount = 2,
        pendingReviewCount = id == ServerId ? 1 : 0,
        shared,
    };

    [Fact] // AC-8
    public void TheServersAreListedWithStatusScopeAndThePendingReviewCount()
    {
        var page = Visit<McpServers>("/tools/mcp");

        var row = page.Find("[data-server=files]").ParentElement!.ParentElement!;
        Assert.Contains("Available", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 to review", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Tenant", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("http://files.test/mcp", row.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-3
    public void TheFiltersTravelToTheServerAndToTheAddress()
    {
        var page = Visit<McpServers>("/tools/mcp?status=Unavailable&scope=global");

        Assert.Contains(Api.Queries, q => q.Contains("status=Unavailable", StringComparison.Ordinal) && q.Contains("scope=global", StringComparison.Ordinal) && q.Contains("limit=50", StringComparison.Ordinal));
        Assert.NotEmpty(page.FindAll("[data-filter]"));
    }

    [Fact] // AC-11
    public void AFailedListShowsTheProblemWithARetryAndTheRetryReloads()
    {
        _listStatus = HttpStatusCode.InternalServerError;
        var page = Visit<McpServers>("/tools/mcp");
        Assert.Contains("Try again", page.Markup, StringComparison.Ordinal);

        _listStatus = HttpStatusCode.OK;
        ClickButton(page, "Try again");

        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-server=files]")));
    }

    [Fact] // AC-11
    public void AnEmptyListInvitesToAddAServer()
    {
        Api.Respond = (_, path) => path == "/api/tools/servers" ? Page() : new HttpResponseMessage(HttpStatusCode.NotFound);

        var page = Visit<McpServers>("/tools/mcp");

        Assert.Contains("No MCP server is registered yet", page.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC-5
    public void ANextPageIsAddedToTheRowsOnLoadMore()
    {
        _includeShared = true;
        _nextCursor = "next";
        var page = Visit<McpServers>("/tools/mcp");
        Assert.Single(page.FindAll("[data-server=files]"));

        ClickButton(page, "Load more");

        page.WaitForAssertion(() => Assert.Equal(2, Api.Queries.Count(q => q.Contains("cursor=next", StringComparison.Ordinal)) + 1));
    }

    [Fact] // AC-3
    public void OpeningAServerShowsItsFormAndItsToolsWithTheReviewControls()
    {
        var page = Visit<McpServers>($"/tools/mcp/{ServerId}", p => p.Add(c => c.Id, ServerId.ToString()));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-tool=read]")));
        var row = page.Find("[data-tool=read]");
        Assert.Contains("Needs review", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("read-only", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Approve and enable", row.TextContent, StringComparison.Ordinal);
        Assert.Equal("files", page.Find("[data-form=server] input[type=text]").GetAttribute("value"));
    }

    [Fact] // AC-9
    public void ApprovingAPendingToolEnablesItWithTheChosenPolicy()
    {
        var page = Visit<McpServers>($"/tools/mcp/{ServerId}", p => p.Add(c => c.Id, ServerId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-tool=read]")));
        page.Find("[data-tool=read] select").Change(Quorum.Web.Client.Api.ToolPolicies.Auto);

        page.Find("[data-tool=read]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Approve and enable", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/tools/{ReadTool}")));
        var body = Body(Api, HttpMethod.Put, $"/api/tools/{ReadTool}");
        Assert.Equal(("Enabled", "Auto"), (body.GetProperty("state").GetString(), body.GetProperty("policy").GetString()));
    }

    [Fact] // AC-9
    public void OnlyThePendingToolsAreAskedForWithTheToReviewFilter()
    {
        var page = Visit<McpServers>($"/tools/mcp/{ServerId}", p => p.Add(c => c.Id, ServerId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-tool=read]")));

        page.Find("[data-filter=to-review]").Click();

        page.WaitForAssertion(() => Assert.Contains(Api.Queries, q => q.Contains("status=PendingReview", StringComparison.Ordinal)));
    }

    [Fact] // spec 004 D-25
    public void AnUnavailableServerShowsWhyAndThatItsToolsAreNotOffered()
    {
        _status = "Unavailable";
        _lastError = "Connection refused";

        var page = Visit<McpServers>($"/tools/mcp/{ServerId}", p => p.Add(c => c.Id, ServerId.ToString()));

        page.WaitForAssertion(() =>
        {
            Assert.Contains("none of its tools are offered", page.Markup, StringComparison.Ordinal);
            Assert.Contains("Connection refused", page.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RefreshingShowsWhatWasAddedChangedAndRemoved()
    {
        var page = Visit<McpServers>($"/tools/mcp/{ServerId}", p => p.Add(c => c.Id, ServerId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=server]")));

        ClickButton(page, "Refresh");

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Added: list.", page.Markup, StringComparison.Ordinal);
            Assert.Contains("Changed: read.", page.Markup, StringComparison.Ordinal);
        });
        Assert.Equal(1, Api.Count(HttpMethod.Post, $"/api/tools/servers/{ServerId}/refresh"));
    }

    [Fact] // AC-16
    public void DeletingAsksFirstAndThenRemovesTheServer()
    {
        var page = Visit<McpServers>($"/tools/mcp/{ServerId}", p => p.Add(c => c.Id, ServerId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=server]")));

        ClickButton(page, "Delete");
        Assert.Equal(0, Api.Count(HttpMethod.Delete, "/api/tools/servers"));
        Dialogs.WaitForAssertion(() => Assert.Contains("removed from every assistant", Dialogs.Markup, StringComparison.Ordinal));
        ClickButton(Dialogs, "Delete server");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Delete, $"/api/tools/servers/{ServerId}")));
    }

    [Fact] // AC-16
    public void CancellingTheDeleteDialogKeepsTheServer()
    {
        var page = Visit<McpServers>($"/tools/mcp/{ServerId}", p => p.Add(c => c.Id, ServerId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=server]")));

        ClickButton(page, "Delete");
        Dialogs.WaitForAssertion(() => Assert.Contains("Cancel", Dialogs.Markup, StringComparison.Ordinal));
        ClickButton(Dialogs, "Cancel");

        Assert.Equal(0, Api.Count(HttpMethod.Delete, "/api/tools/servers"));
    }

    [Fact] // AC-3
    public void AddingAServerTestsTheConnectionAndRegistersWhatWasTyped()
    {
        var page = Visit<McpServers>("/tools/mcp/new", p => p.Add(c => c.Id, "new"));
        var inputs = page.Find("[data-form=server]").QuerySelectorAll("input[type=text]");
        inputs[0].Change("web");
        inputs[1].Change("http://web.test/mcp");

        ClickButton(page, "Test connection");
        page.WaitForAssertion(() => Assert.Contains("Connected. The server offers 1 tool(s): fetch", page.Markup, StringComparison.Ordinal));
        page.Find("[data-form=server]").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Requests.Count(r => r.Method == HttpMethod.Post && r.Path == "/api/tools/servers")));
        var body = Body(Api, HttpMethod.Post, "/api/tools/servers");
        Assert.Equal(("web", "http://web.test/mcp", "Tenant"), (body.GetProperty("name").GetString(), body.GetProperty("url").GetString(), body.GetProperty("scope").GetProperty("kind").GetString()));
    }

    [Fact]
    public void ASharedServerOffersNoEditingAndItsToolsAreReadOnly()
    {
        _includeShared = true;

        var page = Visit<McpServers>($"/tools/mcp/{SharedId}", p => p.Add(c => c.Id, SharedId.ToString()));

        page.WaitForAssertion(() => Assert.Contains("only its owner can change it", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Delete", StringComparison.Ordinal));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Approve and enable", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownServerSaysSo()
    {
        var page = Visit<McpServers>("/tools/mcp/nope", p => p.Add(c => c.Id, "nope"));

        Assert.Contains("There is no such server", page.Markup, StringComparison.Ordinal);
    }
}
