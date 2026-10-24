using System.Net;
using Bunit;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>The lookup tools list and drawer, and the built-in tools page (spec 010 AC-3, AC-9, AC-11, AC-16; spec 007 D-12).</summary>
public sealed class LookupAndBuiltinToolsPageTests : ListPageTestContext
{
    private static readonly Guid ToolId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5001");
    private static readonly Guid SharedTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5002");
    private static readonly Guid LookupId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5011");
    private static readonly Guid BaseId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5021");
    private static readonly Guid Calculator = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5031");
    private static readonly Guid Clock = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5032");

    public LookupAndBuiltinToolsPageTests()
    {
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/tools/lookup")
            {
                return Page(
                    new
                    {
                        id = ToolId,
                        name = "countries",
                        lookups = new[] { new { id = LookupId, name = "nationality" } },
                        knowledgeBases = new[] { new { id = BaseId, name = "policies" } },
                        scope = NoScopeIds("Tenant"),
                        state = "PendingReview",
                        policy = "Confirm",
                        shared = false,
                        warnings = Array.Empty<object>(),
                    },
                    new
                    {
                        id = SharedTool,
                        name = "partner",
                        lookups = Array.Empty<object>(),
                        knowledgeBases = Array.Empty<object>(),
                        scope = NoScopeIds("Global"),
                        state = "Enabled",
                        policy = "Auto",
                        shared = true,
                        warnings = Array.Empty<object>(),
                    });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases")
            {
                return Page(new { id = BaseId, name = "policies", description = "Company policies.", status = "Available", shared = false, kind = "Qdrant", scope = NoScopeIds("Tenant") });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases/dictionary")
            {
                return Page();
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases/lookups")
            {
                return Page(new { id = LookupId, name = "nationality", kind = "Manual", knowledgeBase = (object?)null, field = (string?)null, unit = (string?)null, scope = NoScopeIds("Tenant"), valueCount = 2, shared = false });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases/manual-lookups")
            {
                return Page(new { id = LookupId, name = "nationality", description = (string?)null, scope = NoScopeIds("Tenant"), valueCount = 2, shared = false });
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{BaseId}/lookups")
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            if (path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = Guid.NewGuid(), name = "Assistant" } });
            }

            if (path == "/api/tenants")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (method == HttpMethod.Get && path == "/api/tools")
            {
                return FakeApiHandler.Json(new object[]
                {
                    Builtin(Calculator, "calculator", "Enabled", "Auto"),
                    Builtin(Clock, "current_datetime", "PendingReview", "Confirm"),
                    new { id = Guid.NewGuid(), origin = "Mcp", serverName = "files", name = "read", description = "reads", state = "Enabled", policy = "Auto", annotations = (object?)null },
                });
            }

            if (method == HttpMethod.Put && path.StartsWith("/api/tools/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static object Builtin(Guid id, string name, string state, string policy) =>
        new { id, origin = "Builtin", serverName = (string?)null, name, description = $"{name} does things.", state, policy, annotations = (object?)null };

    [Fact] // AC-3
    public void TheLookupToolsAreListedWithTheirListsAndScope()
    {
        var page = Visit<Quorum.Web.Client.Pages.LookupTools>("/tools/lookup");

        var row = page.Find("[data-lookup-tool=countries]").ParentElement!.ParentElement!;
        Assert.Contains("lookup__countries", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("nationality", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("also asks live: policies", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Approve and enable", row.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-9
    public void ApprovingAToolInTheListEnablesItAndASharedOneIsReadOnly()
    {
        var page = Visit<Quorum.Web.Client.Pages.LookupTools>("/tools/lookup");
        var row = page.Find("[data-lookup-tool=countries]").ParentElement!.ParentElement!;

        row.QuerySelectorAll("button").First(b => b.TextContent.Contains("Approve and enable", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/tools/{ToolId}")));
        Assert.Equal("Enabled", Body(Api, HttpMethod.Put, $"/api/tools/{ToolId}").GetProperty("state").GetString());
        Assert.Empty(page.Find("[data-lookup-tool=partner]").ParentElement!.ParentElement!.QuerySelectorAll("select"));
    }

    [Fact] // AC-11
    public void AnEmptyListInvitesToAddATool()
    {
        var script = Api.Respond;
        Api.Respond = (method, path) => method == HttpMethod.Get && path == "/api/tools/lookup" ? Page() : script(method, path);

        var page = Visit<Quorum.Web.Client.Pages.LookupTools>("/tools/lookup");

        Assert.Contains("No lookup tool is configured yet", page.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC-16
    public void ANewToolOpensTheFormInTheDrawer()
    {
        var page = Visit<Quorum.Web.Client.Pages.LookupTools>("/tools/lookup/new", p => p.Add(c => c.Id, "new"));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=lookup]")));
        Assert.Contains("nationality", page.Find("[data-form=lookup]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-9
    public void TheBuiltInPageListsOnlyTheBuiltInToolsAndChangesTheirPolicy()
    {
        var page = Visit<BuiltinTools>("/tools/builtin");

        page.WaitForAssertion(() => Assert.Equal(["calculator", "current_datetime"], page.FindAll(".tool-row").Select(r => r.GetAttribute("data-tool")).ToArray()));

        page.Find("[data-tool=calculator] select").Change(Quorum.Web.Client.Api.ToolPolicies.Confirm);

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/tools/{Calculator}")));
    }

    [Fact] // AC-9
    public void TheBuiltInPageAsksTheServerForThePendingToolsOnly()
    {
        var page = Visit<BuiltinTools>("/tools/builtin");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".tool-row")));

        page.Find("[data-filter=to-review]").Click();

        page.WaitForAssertion(() => Assert.Contains(Api.Queries, q => q.Contains("status=PendingReview", StringComparison.Ordinal)));
    }
}
