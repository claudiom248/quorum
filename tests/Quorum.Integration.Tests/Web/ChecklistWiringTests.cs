using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Web.Client;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>Opening a page of a step completes it, once, and never breaks the page (spec 010, D-13; AC-13).</summary>
public sealed class ChecklistWiringTests : ListPageTestContext
{
    private static readonly Guid AgentId = Guid.Parse("01a14140-0000-7208-890d-b78925ebb291");

    private bool _promptDone;
    private bool _dismissed;
    private HttpStatusCode _putStatus = HttpStatusCode.NoContent;

    public ChecklistWiringTests()
    {
        Api.Respond = (method, path) => (method.Method, path) switch
        {
            ("GET", "/api/setup/status") => FakeApiHandler.Json(new { isSetupMode = false }),
            ("GET", "/api/auth/me") => FakeApiHandler.Json(new { email = "ada@example.com", displayName = "Ada", role = "Admin" }),
            ("GET", "/api/onboarding/checklist") => FakeApiHandler.Json(new
            {
                items = new object[] { new { key = "model", done = true }, new { key = "prompt", done = _promptDone }, new { key = "tools", done = false }, new { key = "knowledge", done = false } },
                dismissed = _dismissed,
            }),
            ("PUT", _) when path.StartsWith("/api/onboarding/checklist/", StringComparison.Ordinal) => new HttpResponseMessage(_putStatus),
            ("GET", "/api/tools/servers" or "/api/knowledge-bases") => Page(),
            ("GET", "/api/assistants" or "/api/tenants" or "/api/model-endpoints") => FakeApiHandler.Json(Array.Empty<object>()),
            ("GET", "/api/agents") => FakeApiHandler.Json(new[] { new { id = AgentId, slug = "assistant", displayName = "Assistant", description = "d", scope = "Tenant", modelEndpointId = (Guid?)null, subAgentCount = 0, toolCount = 0 } }),
            ("GET", _) when path == $"/api/agents/{AgentId}" => FakeApiHandler.Json(new
            {
                id = AgentId,
                slug = "assistant",
                displayName = "Assistant",
                description = "d",
                systemPrompt = "Be brief.",
                modelEndpointId = (Guid?)null,
                scope = "Tenant",
                scopeTenantIds = Array.Empty<Guid>(),
                subAgents = Array.Empty<object>(),
            }),
            ("GET", _) when path == $"/api/agents/{AgentId}/tools" => FakeApiHandler.Json(new { pool = Array.Empty<object>(), warnings = Array.Empty<object>() }),
            ("GET", "/api/settings/assistant") => FakeApiHandler.Json(new
            {
                baseUrl = "http://ollama:11434/v1",
                modelName = "llama3.2",
                hasApiKey = false,
                contextTokenBudget = 4096,
                systemPrompt = "Be brief.",
                supportsTools = true,
            }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        Services.GetRequiredService<Session>().LoadAsync().GetAwaiter().GetResult();
    }

    // The pages report without waiting for the answer, so the write lands a moment after the page is up.
    private static void Eventually(Action assertion)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                assertion();
                return;
            }
            catch (Xunit.Sdk.XunitException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static string Put(string key) => $"/api/onboarding/checklist/{key}";

    [Fact]
    public void OpeningTheMcpServersPageCompletesTools()
    {
        Visit<McpServers>("/tools/mcp");

        Eventually(() => Assert.Equal(1, Api.Count(HttpMethod.Put, Put("tools"))));
    }

    [Fact]
    public void OpeningTheKnowledgeBasesPageCompletesKnowledge()
    {
        Visit<Quorum.Web.Client.Pages.KnowledgeBases>("/knowledge/bases");

        Eventually(() => Assert.Equal(1, Api.Count(HttpMethod.Put, Put("knowledge"))));
    }

    [Fact]
    public void OpeningAnAgentCompletesPrompt()
    {
        Visit<Quorum.Web.Client.Pages.Agents>($"/agents/{AgentId}", p => p.Add(c => c.Id, AgentId.ToString()));

        Eventually(() => Assert.Equal(1, Api.Count(HttpMethod.Put, Put("prompt"))));
    }
    [Fact]
    public void ADismissedChecklistCostsNoWrite()
    {
        _dismissed = true;

        Visit<McpServers>("/tools/mcp");

        Thread.Sleep(200);
        Assert.Equal(0, Api.Count(HttpMethod.Put, "/api/onboarding/checklist"));
    }

    [Fact]
    public void AStepAlreadyDoneIsNotWrittenAgain()
    {
        _promptDone = true;
        var page = Visit<Quorum.Web.Client.Pages.Agents>($"/agents/{AgentId}", p => p.Add(c => c.Id, AgentId.ToString()));

        page.WaitForAssertion(() => page.Find("[data-form=agent]"));
        Thread.Sleep(200);
        Assert.Equal(0, Api.Count(HttpMethod.Put, Put("prompt")));
    }
    [Fact]
    public void AFailedWriteDoesNotBreakThePage()
    {
        _putStatus = HttpStatusCode.InternalServerError;

        var page = Visit<McpServers>("/tools/mcp");

        Assert.Contains("No MCP server is registered yet", page.Markup, StringComparison.Ordinal);
    }
}

