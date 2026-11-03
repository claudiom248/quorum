using System.Net;
using System.Text.Json;
using Bunit;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>The admin pages of agents and model endpoints: a filtered list and a drawer for each row (spec 013 D-27, D-30).</summary>
public sealed class AgentPagesTests : ListPageTestContext
{
    private static readonly Guid Root = Guid.Parse("01a14120-0000-7208-890d-b78925ebb291");
    private static readonly Guid Endpoint = Guid.Parse("01a14121-0000-7208-890d-b78925ebb291");
    private static readonly Guid AssistantId = Guid.Parse("01a14122-0000-7208-890d-b78925ebb291");
    private static readonly string[] Models = ["m"];

    private static object AgentItem(Guid id, string slug, string scope = "Tenant", Guid? endpoint = null) =>
        new { id, slug, displayName = slug, description = "d", scope, modelEndpointId = endpoint, subAgentCount = 0, toolCount = 0 };

    private static object EndpointItem(Guid id, string name, string model = "m", bool supportsTools = true) =>
        new { id, name, baseUrl = "http://x/v1", modelName = model, hasApiKey = true, contextTokenBudget = 4096, supportsTools };

    private void RespondWithAgents(int others, bool toolsAvailable = true, bool withAssistant = false, IReadOnlyList<object>? items = null, IReadOnlyList<object>? pool = null, IReadOnlyList<object>? warnings = null)
    {
        items ??= [AgentItem(Root, "root"), .. Enumerable.Range(0, others).Select(i => AgentItem(Guid.NewGuid(), $"helper-{i}"))];
        object[] assistants = withAssistant ? [new { id = AssistantId, name = "Desk", rootAgentSlug = "root", maxParallelSubAgents = 1 }] : [];
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/agents")
            {
                return FakeApiHandler.Json(items);
            }

            if (method == HttpMethod.Get && path == "/api/assistants")
            {
                return FakeApiHandler.Json(assistants);
            }

            if (method == HttpMethod.Get && path == "/api/model-endpoints")
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (method == HttpMethod.Get && path == $"/api/agents/{Root}")
            {
                return FakeApiHandler.Json(new
                {
                    id = Root,
                    slug = "root",
                    displayName = "root",
                    description = "d",
                    systemPrompt = "p",
                    modelEndpointId = (Guid?)null,
                    scope = "Tenant",
                    scopeTenantIds = Array.Empty<Guid>(),
                    subAgents = Array.Empty<object>(),
                });
            }

            if (method == HttpMethod.Get && path == $"/api/agents/{Root}/tools")
            {
                return toolsAvailable
                    ? FakeApiHandler.Json(new { pool = pool ?? [], warnings = warnings ?? [] })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
    }

    private IRenderedComponent<Quorum.Web.Client.Pages.Agents> OpenAgent(string id)
    {
        var page = Visit<Quorum.Web.Client.Pages.Agents>($"/agents/{id}", p => p.Add(c => c.Id, id));
        page.WaitForAssertion(() => page.Find("[data-form=agent]"));
        return page;
    }

    private static IEnumerable<string> Rows(IRenderedComponent<Quorum.Web.Client.Pages.Agents> page) =>
        page.FindAll("[data-grid=agents] tbody tr").Select(row => row.QuerySelector("[data-agent]")!.GetAttribute("data-agent")!);

    [Fact]
    public void TheSizeWarningAppearsOnlyAboveTenSubAgentsAndTools()
    {
        RespondWithAgents(others: 11);
        var page = OpenAgent(Root.ToString());
        Assert.Empty(page.FindAll("[data-warning=size]"));

        foreach (var box in page.FindAll("[data-form=agent] input[type=checkbox]").Take(10))
        {
            box.Change(true);
        }

        Assert.Empty(page.FindAll("[data-warning=size]"));
        page.FindAll("[data-form=agent] input[type=checkbox]")[10].Change(true);
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-warning=size]")));
    }

    [Fact]
    public void ASubAgentIsSentByItsSlugWithItsFailurePolicy()
    {
        RespondWithAgents(others: 1, toolsAvailable: false);
        var page = OpenAgent(Root.ToString());
        page.Find("[data-form=agent] input[type=checkbox]").Change(true);
        page.Find("select[aria-label='If helper-0 fails']").Change("AbortTurn");

        page.Find("[data-form=agent]").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/agents/{Root}")));
        using var body = JsonDocument.Parse(Api.LastBody(HttpMethod.Put, $"/api/agents/{Root}")!);
        var edge = Assert.Single(body.RootElement.GetProperty("subAgents").EnumerateArray());
        Assert.Equal("helper-0", edge.GetProperty("slug").GetString());
        Assert.Equal("AbortTurn", edge.GetProperty("failurePolicy").GetString());
    }

    [Fact]
    public void AnAgentOfferedAsAnAssistantCreatesBothFromOneForm()
    {
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && (path is "/api/agents" or "/api/model-endpoints" or "/api/assistants"))
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (method == HttpMethod.Post && path == "/api/agents")
            {
                return FakeApiHandler.Json(new
                {
                    id = Root,
                    slug = "desk",
                    displayName = "Contract desk",
                    description = string.Empty,
                    systemPrompt = string.Empty,
                    modelEndpointId = (Guid?)null,
                    scope = "Tenant",
                    scopeTenantIds = Array.Empty<Guid>(),
                    subAgents = Array.Empty<object>(),
                });
            }

            if (method == HttpMethod.Post && path == "/api/assistants")
            {
                return FakeApiHandler.Json(new { id = Guid.NewGuid(), name = "Contract desk", rootAgentSlug = "desk", maxParallelSubAgents = 1 });
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        var page = OpenAgent("new");
        var fields = page.FindAll("[data-form=agent] input[type=text]");
        fields[0].Change("Contract desk");
        fields[1].Change("desk");
        page.Find("[data-section=assistant] input[type=checkbox]").Change(true);

        page.Find("[data-form=agent]").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Post, "/api/assistants")));
        var assistant = Body(Api, HttpMethod.Post, "/api/assistants");
        Assert.Equal("Contract desk", assistant.GetProperty("name").GetString());
        Assert.Equal("desk", assistant.GetProperty("rootAgentSlug").GetString());
        Assert.Equal(1, Api.Count(HttpMethod.Post, "/api/agents"));
    }

    [Fact]
    public void DeletingAnAgentDeletesTheAssistantThatAnswersThroughIt()
    {
        RespondWithAgents(others: 0, toolsAvailable: false, withAssistant: true);
        var page = OpenAgent(Root.ToString());
        Assert.Contains("Desk", page.Find("[data-grid=agents]").TextContent, StringComparison.Ordinal);

        ClickButton(page, "Delete");
        ClickButton(Dialogs, "Delete agent");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Delete, $"/api/agents/{Root}")));
        Assert.Equal(1, Api.Count(HttpMethod.Delete, $"/api/assistants/{AssistantId}"));
    }

    [Fact]
    public void AnEndpointIsChosenByTheNameItWasGivenInQuorum()
    {
        Api.Respond = (method, path) => method switch
        {
            _ when method == HttpMethod.Get && path == "/api/model-endpoints" => FakeApiHandler.Json(new[] { EndpointItem(Endpoint, "qwen-flash", "qwen3.8-flash") }),
            _ when method == HttpMethod.Get && path is "/api/agents" or "/api/assistants" => FakeApiHandler.Json(Array.Empty<object>()),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        var page = OpenAgent("new");

        var option = page.FindAll("[data-form=agent] select[aria-label='Model endpoint'] option").Single(o => o.GetAttribute("value") == Endpoint.ToString());
        Assert.Equal("qwen-flash", option.TextContent);
    }

    [Theory]
    [InlineData("", "root,planner,analyst")]
    [InlineData("?q=ANAL", "analyst")]
    [InlineData("?scope=global", "planner")]
    [InlineData("?type={endpoint}", "analyst")]
    [InlineData("?type=inherit", "root,planner")]
    [InlineData("?assistant={assistant}", "root")]
    public void TheAgentsAreFilteredBySearchScopeEndpointAndAssistant(string query, string expected)
    {
        RespondWithAgents(
            others: 0,
            withAssistant: true,
            items: [AgentItem(Root, "root"), AgentItem(Guid.NewGuid(), "planner", "Global"), AgentItem(Guid.NewGuid(), "analyst", endpoint: Endpoint)]);

        var page = Visit<Quorum.Web.Client.Pages.Agents>("/agents" + query.Replace("{endpoint}", Endpoint.ToString(), StringComparison.Ordinal).Replace("{assistant}", AssistantId.ToString(), StringComparison.Ordinal));

        Assert.Equal(expected.Split(','), Rows(page));
    }
    [Fact]
    public void AFilterThatMatchesNothingSaysSoAndCanBeCleared()
    {
        RespondWithAgents(others: 2);

        var page = Visit<Quorum.Web.Client.Pages.Agents>("/agents?q=nothing-like-this");

        page.WaitForAssertion(() => Assert.Contains("Nothing matches these filters", page.Find("[data-empty=filtered]").TextContent, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "qwen-flash,legacy")]
    [InlineData("?q=llama", "legacy")]
    [InlineData("?type=yes", "qwen-flash")]
    public void TheEndpointsAreFilteredBySearchAndToolSupport(string query, string expected)
    {
        Api.Respond = (method, path) => method == HttpMethod.Get && path == "/api/model-endpoints"
            ? FakeApiHandler.Json(new[] { EndpointItem(Endpoint, "qwen-flash", "qwen3.8-flash"), EndpointItem(Guid.NewGuid(), "legacy", "llama2", supportsTools: false) })
            : new HttpResponseMessage(HttpStatusCode.NoContent);

        var page = Visit<Quorum.Web.Client.Pages.ModelEndpoints>("/agents/endpoints" + query);

        var names = page.FindAll("[data-grid=endpoints] tbody tr").Select(row => row.QuerySelector("[data-endpoint]")!.GetAttribute("data-endpoint")!);
        Assert.Equal(expected.Split(','), names);
    }
    private IRenderedComponent<Quorum.Web.Client.Pages.ModelEndpoints> OpenEndpoint(bool supportsTools = true, bool? reported = true)
    {
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/model-endpoints")
            {
                return FakeApiHandler.Json(new[] { EndpointItem(Endpoint, "e", supportsTools: supportsTools) });
            }

            return method == HttpMethod.Post && path == "/api/model-endpoints/test"
                ? FakeApiHandler.Json(new { reachable = true, models = Models, supportsTools = reported })
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        var page = Visit<Quorum.Web.Client.Pages.ModelEndpoints>($"/agents/endpoints/{Endpoint}", p => p.Add(c => c.Id, Endpoint.ToString()));
        page.WaitForAssertion(() => page.Find("[data-form=endpoint]"));
        return page;
    }

    [Fact]
    public void TestingAnEndpointBeingEditedNamesItSoItsStoredKeyIsUsed()
    {
        var page = OpenEndpoint();

        ClickButton(page, "Test connection");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Post, "/api/model-endpoints/test")));
        var body = Body(Api, HttpMethod.Post, "/api/model-endpoints/test");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("apiKey").ValueKind);
        Assert.Equal(Endpoint, body.GetProperty("endpointId").GetGuid());
        page.WaitForAssertion(() => Assert.Contains("1 model(s) available", page.Find("[data-form=model]").TextContent, StringComparison.Ordinal));
        Assert.Contains("mud-select", page.Find("[data-form=model]").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndpointKeyIsOmittedToKeepIt()
    {
        var page = OpenEndpoint();

        page.Find("[data-form=endpoint]").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/model-endpoints/{Endpoint}")));
        Assert.Equal(JsonValueKind.Null, Body(Api, HttpMethod.Put, "/api/model-endpoints/").GetProperty("apiKey").ValueKind);
    }

    [Fact]
    public void AnEndpointKeyIsEmptyToRemoveIt()
    {
        var page = OpenEndpoint();

        page.Find("[data-form=endpoint] input[type=checkbox]").Change(true);
        page.Find("[data-form=endpoint]").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/model-endpoints/{Endpoint}")));
        Assert.Equal(string.Empty, Body(Api, HttpMethod.Put, "/api/model-endpoints/").GetProperty("apiKey").GetString());
    }

    private static bool SwitchIsOn(IRenderedComponent<Quorum.Web.Client.Pages.ModelEndpoints> page) => page.Find("[data-form=model] .mud-switch input").HasAttribute("checked");

    [Theory]
    [InlineData(true, true, "supports tools")]
    [InlineData(false, false, "does not support tools")]
    public void TestingTheConnectionPreFillsTheFlagFromWhatTheServerReports(bool reported, bool expected, string hint)
    {
        var page = OpenEndpoint(supportsTools: !reported, reported: reported);

        ClickButton(page, "Test connection");

        page.WaitForAssertion(() =>
        {
            Assert.Contains(hint, page.Find("#tools-support-hint").TextContent, StringComparison.Ordinal);
            Assert.Equal(expected, SwitchIsOn(page));
        });
    }

    [Fact]
    public void WhenTheServerDoesNotSayTheAdminsValueStaysAndTheHintSaysSo()
    {
        var page = OpenEndpoint(supportsTools: false, reported: null);

        ClickButton(page, "Test connection");

        page.WaitForAssertion(() => Assert.Contains("does not say", page.Find("#tools-support-hint").TextContent, StringComparison.Ordinal));
        Assert.False(SwitchIsOn(page));
    }

    [Fact]
    public void SavingAnEndpointSendsTheFlag()
    {
        var page = OpenEndpoint(supportsTools: false);

        page.Find("[data-form=endpoint]").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/model-endpoints/{Endpoint}")));
        Assert.False(Body(Api, HttpMethod.Put, "/api/model-endpoints/").GetProperty("supportsTools").GetBoolean());
    }

    private static readonly Guid Calculator = Guid.Parse("01a14130-0000-7208-890d-b78925ebb291");
    private static readonly Guid Clock = Guid.Parse("01a14131-0000-7208-890d-b78925ebb291");
    private static readonly Guid LookupTool = Guid.Parse("01a14132-0000-7208-890d-b78925ebb291");

    [Fact]
    public void SavingAnAgentSendsExactlyTheActiveToolsAndTheManifestOfTheLookupOnes()
    {
        RespondWithAgents(
            others: 0,
            pool:
            [
                new { toolId = Calculator, name = "builtin__calculator", active = true },
                new { toolId = Clock, name = "builtin__current_datetime", active = false },
                new { toolId = LookupTool, name = "lookup__contracts", active = true, manifest = "Full" },
            ]);
        var page = OpenAgent(Root.ToString());

        // Swap the calculator for the clock, and offer the lookup lean.
        var boxes = page.FindAll("[data-tool-picker] .mud-checkbox input");
        boxes[0].Change(false);
        boxes[1].Change(true);
        page.Find("[data-tool-picker] .mud-switch input").Change(true);
        page.Find("[data-form=agent]").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/agents/{Root}/tools")));
        var body = Body(Api, HttpMethod.Put, $"/api/agents/{Root}/tools");
        Assert.Equal([Clock.ToString(), LookupTool.ToString()], body.GetProperty("toolIds").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Equal("lean", body.GetProperty("manifests").GetProperty(LookupTool.ToString()).GetString());
    }

    [Fact]
    public void TheWarningsOfTheServerAreShownAboutTheActiveSet()
    {
        RespondWithAgents(
            others: 0,
            pool: [new { toolId = Calculator, name = "builtin__calculator", active = true }],
            warnings: [new { code = "tool.too_many", message = "More than 15 tools are active: small models choose worse among many." }]);

        var page = OpenAgent(Root.ToString());

        Assert.Contains("More than 15 tools are active", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEndpointsPageWarnsWhenTheSetupCouldNotReachTheModel()
    {
        Api.Respond = (method, path) => method == HttpMethod.Get && path == "/api/model-endpoints"
            ? FakeApiHandler.Json(new[] { EndpointItem(Endpoint, "e") })
            : new HttpResponseMessage(HttpStatusCode.NoContent);

        var page = Visit<Quorum.Web.Client.Pages.ModelEndpoints>("/agents/endpoints?unverified=1");

        Assert.Contains("could not be reached during setup", page.Markup, StringComparison.Ordinal);
    }
}