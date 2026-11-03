using System.Net;
using System.Text.Json;
using Bunit;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>The admin pages of agents, assistants and model endpoints (spec 013 D-27, D-30).</summary>
public sealed class AgentPagesTests : ListPageTestContext
{
    private static readonly Guid Root = Guid.Parse("01a14120-0000-7208-890d-b78925ebb291");
    private static readonly Guid Endpoint = Guid.Parse("01a14121-0000-7208-890d-b78925ebb291");

    private static readonly string[] Models = ["m"];

    private static object AgentItem(Guid id, string slug) =>
        new { id, slug, displayName = slug, description = "d", scope = "Tenant", modelEndpointId = (Guid?)null, subAgentCount = 0, toolCount = 0 };

    private void RespondWithAgents(int others, bool toolsAvailable = true)
    {
        var items = new List<object> { AgentItem(Root, "root") };
        items.AddRange(Enumerable.Range(0, others).Select(i => AgentItem(Guid.NewGuid(), $"helper-{i}")));
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/agents")
            {
                return FakeApiHandler.Json(items);
            }

            if (method == HttpMethod.Get && (path is "/api/model-endpoints" or "/api/assistants"))
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
                    ? FakeApiHandler.Json(new { pool = Array.Empty<object>(), warnings = Array.Empty<object>() })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
    }

    [Fact]
    public void TheSizeWarningAppearsOnlyAboveTenSubAgentsAndTools()
    {
        RespondWithAgents(others: 11);
        var page = Visit<Quorum.Web.Client.Pages.Agents>("/agents");
        page.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        page.WaitForAssertion(() => page.Find("[data-form=agent]"));
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
        var page = Visit<Quorum.Web.Client.Pages.Agents>("/agents");
        page.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        page.WaitForAssertion(() => page.Find("[data-form=agent]"));
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
        var page = Visit<Quorum.Web.Client.Pages.Agents>("/agents");
        ClickButton(page, "Add agent");
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
        var assistantId = Guid.Parse("01a14122-0000-7208-890d-b78925ebb291");
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/agents")
            {
                return FakeApiHandler.Json(new[] { AgentItem(Root, "root") });
            }

            if (method == HttpMethod.Get && path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = assistantId, name = "Desk", rootAgentSlug = "root", maxParallelSubAgents = 1 } });
            }

            return method == HttpMethod.Get && path == "/api/model-endpoints"
                ? FakeApiHandler.Json(Array.Empty<object>())
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        var page = Visit<Quorum.Web.Client.Pages.Agents>("/agents");
        Assert.Contains("Desk", page.Find("[data-grid=agents]").TextContent, StringComparison.Ordinal);

        ClickButton(page, "Delete");
        ClickButton(Dialogs, "Delete");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Delete, $"/api/agents/{Root}")));
        Assert.Equal(1, Api.Count(HttpMethod.Delete, $"/api/assistants/{assistantId}"));
    }

    [Fact]
    public void AnEndpointIsChosenByTheNameItWasGivenInQuorum()
    {
        Api.Respond = (method, path) => method == HttpMethod.Get && path == "/api/model-endpoints"
            ? FakeApiHandler.Json(new[] { new { id = Endpoint, name = "qwen-flash", baseUrl = "http://x/v1", modelName = "qwen3.8-flash", hasApiKey = true, contextTokenBudget = 4096, supportsTools = true } })
            : method == HttpMethod.Get && (path is "/api/agents" or "/api/assistants")
                ? FakeApiHandler.Json(Array.Empty<object>())
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        var page = Visit<Quorum.Web.Client.Pages.Agents>("/agents");

        ClickButton(page, "Add agent");

        var option = page.FindAll("[data-form=agent] select[aria-label='Model endpoint'] option").Single(o => o.GetAttribute("value") == Endpoint.ToString());
        Assert.Equal("qwen-flash", option.TextContent);
    }

    [Fact]
    public void TestingAnEndpointBeingEditedNamesItSoItsStoredKeyIsUsed()
    {
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/model-endpoints")
            {
                return FakeApiHandler.Json(new[] { new { id = Endpoint, name = "e", baseUrl = "http://x/v1", modelName = "m", hasApiKey = true, contextTokenBudget = 4096, supportsTools = true } });
            }

            return method == HttpMethod.Post && path == "/api/model-endpoints/test"
                ? FakeApiHandler.Json(new { reachable = true, models = Models, supportsTools = true })
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        var page = Visit<Quorum.Web.Client.Pages.ModelEndpoints>("/agents/endpoints");
        ClickButton(page, "Edit");

        ClickButton(page, "Test connection");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Post, "/api/model-endpoints/test")));
        var body = Body(Api, HttpMethod.Post, "/api/model-endpoints/test");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("apiKey").ValueKind);
        Assert.Equal(Endpoint, body.GetProperty("endpointId").GetGuid());
    }

    [Fact]
    public void AnEndpointKeyIsOmittedToKeepItAndEmptyToRemoveIt()
    {
        Api.Respond = (method, path) => method == HttpMethod.Get && path == "/api/model-endpoints"
            ? FakeApiHandler.Json(new[] { new { id = Endpoint, name = "e", baseUrl = "http://x/v1", modelName = "m", hasApiKey = true, contextTokenBudget = 4096, supportsTools = true } })
            : new HttpResponseMessage(HttpStatusCode.NoContent);
        var page = Visit<Quorum.Web.Client.Pages.ModelEndpoints>("/agents/endpoints");

        page.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        page.Find("[data-form=endpoint]").Submit();
        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/model-endpoints/{Endpoint}")));
        using (var kept = JsonDocument.Parse(Api.LastBody(HttpMethod.Put, "/api/model-endpoints/")!))
        {
            Assert.Equal(JsonValueKind.Null, kept.RootElement.GetProperty("apiKey").ValueKind);
        }

        page.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        page.Find("[data-form=endpoint] input[type=checkbox]").Change(true);
        page.Find("[data-form=endpoint]").Submit();
        page.WaitForAssertion(() => Assert.Equal(2, Api.Count(HttpMethod.Put, $"/api/model-endpoints/{Endpoint}")));
        using var removed = JsonDocument.Parse(Api.LastBody(HttpMethod.Put, "/api/model-endpoints/")!);
        Assert.Equal(string.Empty, removed.RootElement.GetProperty("apiKey").GetString());
    }
}
