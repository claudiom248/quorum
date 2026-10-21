using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client.Api;
using ToolsPage = Quorum.Web.Client.Pages.Tools;

namespace Quorum.Integration.Tests.Web;

/// <summary>The admin page for servers and tools (spec 004, AC-48, D-14, D-19, D-25).</summary>
public sealed class ToolsPageTests : BunitContext
{
    private static readonly Guid ServerId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1101");
    private static readonly Guid SharedId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1102");
    private static readonly Guid ReadTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1111");
    private static readonly Guid SearchTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1112");
    private static readonly Guid CalculatorTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1113");
    private static readonly Guid AssistantId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b1121");

    private static readonly string[] Added = ["list"];
    private static readonly string[] Changed = ["read"];

    private readonly FakeApiHandler _api = new();
    private string _status = "Available";
    private string? _lastError;
    private bool _includeShared;
    private HttpStatusCode _serversStatus = HttpStatusCode.OK;

    public ToolsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new QuorumApi(_api.CreateClient()));

        _api.Respond = (method, path) =>
        {
            // The rag tools section of the page loads its own lists: none here.
            if (method == HttpMethod.Get && path is "/api/tools/rag" or "/api/knowledge-bases" or "/api/knowledge-bases/dictionary")
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (path == "/api/tools/servers" && method == HttpMethod.Get)
            {
                return _serversStatus == HttpStatusCode.OK
                    ? FakeApiHandler.Json(ServerList().Select(s => Server(s.Id, s.Name, s.Shared, withTools: false)).ToArray())
                    : new HttpResponseMessage(_serversStatus);
            }

            if (method == HttpMethod.Get && path.StartsWith("/api/tools/servers/", StringComparison.Ordinal))
            {
                var server = ServerList().SingleOrDefault(s => path.EndsWith(s.Id.ToString(), StringComparison.Ordinal));
                return server == default
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : FakeApiHandler.Json(Server(server.Id, server.Name, server.Shared, withTools: true));
            }

            if (path == "/api/tools" && method == HttpMethod.Get)
            {
                return FakeApiHandler.Json(new[] { Tool(CalculatorTool, "Builtin", null, "calculator", "Enabled", "Auto", null) });
            }

            if (path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = AssistantId, name = "Assistant" } });
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
                return FakeApiHandler.Json(new
                {
                    reachable = true,
                    tools = new[] { new { name = "fetch", description = "Fetches.", annotations = new { readOnlyHint = true } } },
                });
            }

            if (method == HttpMethod.Post && path == "/api/tools/servers")
            {
                return FakeApiHandler.Json(
                    new
                    {
                        id = Guid.NewGuid(),
                        status = "Available",
                        warning = (string?)null,
                        tools = new[] { Tool(Guid.NewGuid(), "Mcp", "web", "fetch", "PendingReview", "Confirm", null) },
                    },
                    HttpStatusCode.Created);
            }

            if ((method == HttpMethod.Put || method == HttpMethod.Delete) && path.StartsWith("/api/tools/servers/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private (Guid Id, string Name, bool Shared)[] ServerList() =>
        _includeShared ? [(ServerId, "files", false), (SharedId, "partner", true)] : [(ServerId, "files", false)];

    private static object Tool(Guid id, string origin, string? serverName, string name, string state, string policy, object? annotations) =>
        new { id, origin, serverName, name, description = $"{name} does things.", state, policy, annotations };

    private object Server(Guid id, string name, bool shared, bool withTools)
    {
        object[] tools = id == ServerId
            ?
            [
                Tool(ReadTool, "Mcp", name, "read", "PendingReview", "Confirm", new { readOnlyHint = true }),
                Tool(SearchTool, "Mcp", name, "search", "Enabled", "Auto", null),
            ]
            : [];

        return new
        {
            id,
            name,
            url = $"http://{name}.test/mcp",
            hasHeaders = !shared,
            status = shared ? "Available" : _status,
            lastError = shared ? null : _lastError,
            lastRefreshedAt = (DateTimeOffset?)null,
            scope = new { kind = shared ? "Global" : "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
            toolCount = tools.Length,
            pendingReviewCount = id == ServerId ? 1 : 0,
            shared,
            tools = withTools ? tools : null,
        };
    }

    private IRenderedComponent<ToolsPage> RenderPage()
    {
        var page = Render<ToolsPage>();
        page.WaitForAssertion(() => Assert.DoesNotContain("mud-progress-linear", page.Markup, StringComparison.Ordinal));
        return page;
    }

    private static void Click(IRenderedComponent<ToolsPage> page, string text) =>
        page.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    private static void ClickIn(IRenderedComponent<ToolsPage> page, string selector, string text) =>
        page.Find(selector).QuerySelectorAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    private int Registrations() => _api.Requests.Count(r => r.Method == HttpMethod.Post && r.Path == "/api/tools/servers");

    private static JsonElement Body(FakeApiHandler api, HttpMethod method, string path) =>
        JsonDocument.Parse(api.LastBody(method, path)!).RootElement.Clone();

    private static void FillServerForm(IRenderedComponent<ToolsPage> page, string name, string url)
    {
        var inputs = page.Find("[data-form=server]").QuerySelectorAll("input[type=text]");
        inputs[0].Change(name);
        inputs[1].Change(url);
    }

    [Fact] // AC-48
    public void TheServersTheirStatusAndTheirToolsAreListedWithThePendingReviewCount()
    {
        var page = RenderPage();

        var server = page.Find("[data-server=files]");
        Assert.Contains("Available", server.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 to review", server.TextContent, StringComparison.Ordinal);
        Assert.Contains("This tenant", server.TextContent, StringComparison.Ordinal);
        Assert.Contains("headers stored", server.TextContent, StringComparison.Ordinal);
        Assert.Equal(["read", "search"], server.QuerySelectorAll(".tool-row").Select(r => r.GetAttribute("data-tool")).ToArray());
        Assert.Single(page.FindAll("[data-tool=calculator]"));
    }

    [Fact] // AC-48, D-14
    public void AToolPendingReviewIsMarkedWithItsHintsAndOffersApproval()
    {
        var page = RenderPage();

        var row = page.Find("[data-tool=read]");
        Assert.Contains("Needs review", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("read-only", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("claimed by the server", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Approve and enable", row.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-48, D-14
    public void ApprovingAPendingToolEnablesItWithTheChosenPolicy()
    {
        var page = RenderPage();
        page.Find("[data-tool=read] select").Change(ToolPolicies.Auto);

        ClickIn(page, "[data-tool=read]", "Approve and enable");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/{ReadTool}")));
        var body = Body(_api, HttpMethod.Put, $"/api/tools/{ReadTool}");
        Assert.Equal(("Enabled", "Auto"), (body.GetProperty("state").GetString(), body.GetProperty("policy").GetString()));
    }

    [Fact] // AC-48, D-14
    public void ApprovingWithoutTouchingThePolicySendsConfirm()
    {
        var page = RenderPage();

        ClickIn(page, "[data-tool=read]", "Approve and enable");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/{ReadTool}")));
        var body = Body(_api, HttpMethod.Put, $"/api/tools/{ReadTool}");
        Assert.Equal(("Enabled", "Confirm"), (body.GetProperty("state").GetString(), body.GetProperty("policy").GetString()));
    }

    [Fact]
    public void KeepingAPendingToolDisabledSendsDisabled()
    {
        var page = RenderPage();

        ClickIn(page, "[data-tool=read]", "Keep disabled");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/{ReadTool}")));
        Assert.Equal("Disabled", Body(_api, HttpMethod.Put, $"/api/tools/{ReadTool}").GetProperty("state").GetString());
    }

    [Fact] // AC-48
    public void AnEnabledToolIsDisabledWithItsPolicyKept()
    {
        var page = RenderPage();

        page.Find("[data-tool=search] .mud-switch input").Change(false);

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/{SearchTool}")));
        var body = Body(_api, HttpMethod.Put, $"/api/tools/{SearchTool}");
        Assert.Equal(("Disabled", "Auto"), (body.GetProperty("state").GetString(), body.GetProperty("policy").GetString()));
    }

    [Fact] // AC-48
    public void ThePolicyOfAnEnabledToolIsChangedWithItsStateKept()
    {
        var page = RenderPage();

        page.Find("[data-tool=search] select").Change(ToolPolicies.Confirm);

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/{SearchTool}")));
        var body = Body(_api, HttpMethod.Put, $"/api/tools/{SearchTool}");
        Assert.Equal(("Enabled", "Confirm"), (body.GetProperty("state").GetString(), body.GetProperty("policy").GetString()));
    }

    [Fact]
    public void ABuiltinToolIsManagedLikeAnyOther()
    {
        var page = RenderPage();

        page.Find("[data-tool=calculator] select").Change(ToolPolicies.Confirm);

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/{CalculatorTool}")));
    }

    [Fact] // D-25
    public void AnUnavailableServerShowsWhyAndThatItsToolsAreNotOffered()
    {
        _status = "Unavailable";
        _lastError = "Connection refused";

        var page = RenderPage();

        var server = page.Find("[data-server=files]");
        Assert.Contains("Unavailable", server.TextContent, StringComparison.Ordinal);
        Assert.Contains("none of its tools are offered", server.TextContent, StringComparison.Ordinal);
        Assert.Contains("Connection refused", server.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-48, AC-7
    public void RefreshingShowsWhatWasAddedChangedAndRemoved()
    {
        var page = RenderPage();

        Click(page, "Refresh");

        page.WaitForAssertion(() =>
        {
            var server = page.Find("[data-server=files]").TextContent;
            Assert.Contains("Added: list.", server, StringComparison.Ordinal);
            Assert.Contains("Changed: read.", server, StringComparison.Ordinal);
            Assert.DoesNotContain("Removed:", server, StringComparison.Ordinal);
        });
        Assert.Equal(1, _api.Count(HttpMethod.Post, $"/api/tools/servers/{ServerId}/refresh"));
    }

    [Fact]
    public void AFailedRefreshShowsTheProblemAndTheServerAsUnavailable()
    {
        var script = _api.Respond;
        _api.Respond = (method, path) =>
        {
            if (path.EndsWith("/refresh", StringComparison.Ordinal))
            {
                _status = "Unavailable";
                _lastError = "Timed out";
                return FakeApiHandler.Json(
                    new { title = "Unprocessable", detail = "The server did not answer.", code = "tool.server_unreachable" },
                    HttpStatusCode.UnprocessableEntity);
            }

            return script(method, path);
        };
        var page = RenderPage();

        Click(page, "Refresh");

        page.WaitForAssertion(() =>
        {
            Assert.Contains("The server did not answer.", page.Markup, StringComparison.Ordinal);
            Assert.Contains("Timed out", page.Find("[data-server=files]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void DeletingAServerAsksFirstAndThenRemovesIt()
    {
        var page = RenderPage();

        Click(page, "Delete");
        Assert.Equal(0, _api.Count(HttpMethod.Delete, "/api/tools/servers"));
        Assert.Contains("removed from every assistant", page.Markup, StringComparison.Ordinal);
        Click(page, "Delete server");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Delete, $"/api/tools/servers/{ServerId}")));
        page.WaitForAssertion(() => Assert.Contains("files was deleted.", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void CancellingTheDeletionSendsNothing()
    {
        var page = RenderPage();
        Click(page, "Delete");

        page.FindAll("button").Last(b => b.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click();

        Assert.Equal(0, _api.Count(HttpMethod.Delete, "/api/tools/servers"));
        Assert.DoesNotContain("Delete server", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerSharedByAnotherTenantIsReadOnly()
    {
        _includeShared = true;

        var page = RenderPage();

        var shared = page.Find("[data-server=partner]");
        Assert.Contains("Shared with you", shared.TextContent, StringComparison.Ordinal);
        Assert.Empty(shared.QuerySelectorAll("button"));
    }

    [Fact] // AC-48
    public void RegisteringTestsTheConnectionThenSendsNameUrlHeadersAndScope()
    {
        var page = RenderPage();
        Click(page, "Add server");
        FillServerForm(page, "web", "http://web.test/mcp");
        page.Find("[data-form=server] textarea").Change("Authorization: Bearer abc");

        Click(page, "Test connection");
        page.WaitForAssertion(() => Assert.Contains("The server offers 1 tool(s): fetch", page.Markup, StringComparison.Ordinal));
        Assert.Contains("fetch claims: read-only", page.Markup, StringComparison.Ordinal);
        var tested = Body(_api, HttpMethod.Post, "/api/tools/servers/test");
        Assert.Equal("http://web.test/mcp", tested.GetProperty("url").GetString());
        Assert.Equal("Bearer abc", tested.GetProperty("headers").GetProperty("Authorization").GetString());

        page.Find("[data-form=server] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Registrations()));
        var sent = Body(_api, HttpMethod.Post, "/api/tools/servers");
        Assert.Equal("web", sent.GetProperty("name").GetString());
        Assert.Equal("http://web.test/mcp", sent.GetProperty("url").GetString());
        Assert.Equal("Bearer abc", sent.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("Tenant", sent.GetProperty("scope").GetProperty("kind").GetString());
        page.WaitForAssertion(() => Assert.Contains("Review its 1 tool(s)", page.Markup, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-form=server]"));
    }

    [Fact] // AC-12
    public void AnAssistantScopeSendsTheChosenAssistants()
    {
        var page = RenderPage();
        Click(page, "Add server");
        FillServerForm(page, "web", "http://web.test/mcp");
        page.Find("[data-form=server] select").Change(ScopeKinds.Assistant);

        page.Find("[data-form=server] .mud-checkbox input").Change(true);
        page.Find("[data-form=server] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Registrations()));
        var scope = Body(_api, HttpMethod.Post, "/api/tools/servers").GetProperty("scope");
        Assert.Equal("Assistant", scope.GetProperty("kind").GetString());
        Assert.Equal([AssistantId.ToString()], scope.GetProperty("assistantIds").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void AMalformedHeaderLineIsRefusedBeforeAnythingIsSent()
    {
        var page = RenderPage();
        Click(page, "Add server");
        FillServerForm(page, "web", "http://web.test/mcp");
        page.Find("[data-form=server] textarea").Change("not a header");

        page.Find("[data-form=server] form").Submit();

        page.WaitForAssertion(() => Assert.Contains("is not a header", page.Markup, StringComparison.Ordinal));
        Assert.Equal(0, Registrations());
    }

    [Fact]
    public void ARejectedRegistrationKeepsTheFormAndShowsTheServersReason()
    {
        var script = _api.Respond;
        _api.Respond = (method, path) =>
            method == HttpMethod.Post && path == "/api/tools/servers"
                ? FakeApiHandler.Json(new { title = "Bad", detail = "The address of the server is not allowed.", code = "tool.url_blocked" }, HttpStatusCode.BadRequest)
                : script(method, path);
        var page = RenderPage();
        Click(page, "Add server");
        FillServerForm(page, "web", "http://169.254.169.254/");

        page.Find("[data-form=server] form").Submit();

        page.WaitForAssertion(() => Assert.Contains("is not allowed", page.Markup, StringComparison.Ordinal));
        Assert.NotEmpty(page.FindAll("[data-form=server]"));
    }

    [Fact]
    public void EditingKeepsTheStoredHeadersUnlessRemovalIsAsked()
    {
        var page = RenderPage();
        Click(page, "Edit");
        Assert.Contains("Stored headers are kept", page.Markup, StringComparison.Ordinal);

        page.Find("[data-form=server] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/servers/{ServerId}")));
        var kept = Body(_api, HttpMethod.Put, $"/api/tools/servers/{ServerId}");
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("headers").ValueKind);
        Assert.Equal("http://files.test/mcp", kept.GetProperty("url").GetString());
        page.WaitForAssertion(() => Assert.Contains("files was saved.", page.Markup, StringComparison.Ordinal));

        Click(page, "Edit");
        page.Find("[data-form=server] .mud-checkbox input").Change(true);
        page.Find("[data-form=server] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(2, _api.Count(HttpMethod.Put, $"/api/tools/servers/{ServerId}")));
        var removed = Body(_api, HttpMethod.Put, $"/api/tools/servers/{ServerId}");
        Assert.Empty(removed.GetProperty("headers").EnumerateObject());
    }

    [Fact]
    public void ChangingTheAddressSaysTheServerIsUnavailableUntilRefreshed()
    {
        var page = RenderPage();
        Click(page, "Edit");
        page.Find("[data-form=server]").QuerySelectorAll("input[type=text]")[1].Change("http://elsewhere.test/mcp");

        page.Find("[data-form=server] form").Submit();

        page.WaitForAssertion(() => Assert.Contains("unavailable until you refresh it", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void EachSwitchIsNamedAfterItsTool()
    {
        var page = RenderPage();

        Assert.Contains("Enabled (search)", page.Find("[data-tool=search] .mud-switch").TextContent, StringComparison.Ordinal);
        Assert.Contains("Enabled (calculator)", page.Find("[data-tool=calculator] .mud-switch").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerWhoseToolsCouldNotBeLoadedSaysSoInsteadOfOfferingNone()
    {
        var script = _api.Respond;
        _api.Respond = (method, path) =>
            method == HttpMethod.Get && path == $"/api/tools/servers/{ServerId}"
                ? FakeApiHandler.Json(new { title = "Down", detail = "The detail timed out." }, HttpStatusCode.GatewayTimeout)
                : script(method, path);

        var page = RenderPage();

        var server = page.Find("[data-server=files]").TextContent;
        Assert.Contains("could not be loaded: The detail timed out.", server, StringComparison.Ordinal);
        Assert.DoesNotContain("offers no tools", server, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingAServerWithStoredHeadersSaysTheTestDoesNotUseThem()
    {
        var page = RenderPage();
        Assert.Empty(page.FindAll("[data-form=server]"));

        Click(page, "Add server");
        Assert.DoesNotContain("not the stored ones", page.Markup, StringComparison.Ordinal);
        Click(page, "Cancel");
        Click(page, "Edit");

        Assert.Contains("not the stored ones", page.Find("[data-form=server]").TextContent, StringComparison.Ordinal);
    }

    private static bool SwitchIsOn(IRenderedComponent<ToolsPage> page, string tool)
    {
        var input = page.Find($"[data-tool={tool}] .mud-switch input");
        return input.HasAttribute("checked") || input.GetAttribute("aria-checked") == "true";
    }

    [Fact]
    public void ARejectedToolUpdateShowsTheProblemAndTheControlsMatchTheServer()
    {
        var script = _api.Respond;
        _api.Respond = (method, path) =>
            method == HttpMethod.Put && path == $"/api/tools/{SearchTool}"
                ? FakeApiHandler.Json(new { title = "Conflict", detail = "The tool changed meanwhile.", code = "tool.changed" }, HttpStatusCode.Conflict)
                : script(method, path);
        var page = RenderPage();
        Assert.True(SwitchIsOn(page, "search"));

        page.Find("[data-tool=search] .mud-switch input").Change(false);

        page.WaitForAssertion(() => Assert.Contains("The tool changed meanwhile.", page.Markup, StringComparison.Ordinal));
        Assert.True(SwitchIsOn(page, "search"));
    }

    [Fact]
    public void TypingHeadersAndRemovingTheStoredOnesTogetherIsRefused()
    {
        var page = RenderPage();
        Click(page, "Edit");
        page.Find("[data-form=server] textarea").Change("X-New: 1");
        page.Find("[data-form=server] .mud-checkbox input").Change(true);

        page.Find("[data-form=server] form").Submit();

        page.WaitForAssertion(() => Assert.Contains("not both", page.Markup, StringComparison.Ordinal));
        Assert.Equal(0, _api.Count(HttpMethod.Put, $"/api/tools/servers/{ServerId}"));
    }

    [Fact]
    public void AFailedReloadAfterAChangeKeepsTheDataAndTheNoticeOnScreen()
    {
        var page = RenderPage();
        var script = _api.Respond;
        _api.Respond = (method, path) =>
            method == HttpMethod.Get && path == "/api/tools"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : script(method, path);
        Click(page, "Delete");

        Click(page, "Delete server");

        page.WaitForAssertion(() => Assert.Contains("files was deleted.", page.Markup, StringComparison.Ordinal));
        Assert.NotEmpty(page.FindAll("[data-server=files]"));
        Assert.DoesNotContain("Try again", page.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(page.FindAll(".mud-alert-text-error, .mud-alert-outlined-error"));
    }

    [Fact]
    public void WithoutPermissionTheProblemIsShownAndNothingElse()
    {
        _serversStatus = HttpStatusCode.Forbidden;

        var page = RenderPage();

        Assert.DoesNotContain("Add server", page.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(page.FindAll(".mud-alert"));
    }
}
