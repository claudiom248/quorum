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
