using System.Net;
using Bunit;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>The rag tools list and its drawer (spec 010 AC-3, AC-9, AC-11, AC-16; spec 003 AC-19 to AC-23, AC-49).</summary>
public sealed class RagToolsPageTests : ListPageTestContext
{
    private static readonly Guid DepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4001");
    private static readonly Guid YearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4002");
    private static readonly Guid OwnBase = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4011");
    private static readonly Guid OtherBase = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4012");
    private static readonly Guid ToolId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4021");
    private static readonly Guid SharedTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4022");
    private static readonly Guid CreatedTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4023");

    private static readonly Guid[] OwnOnly = [OwnBase];
    private static readonly Guid[] BothBases = [OwnBase, OtherBase];
    private static readonly Guid[] DepartmentOnly = [DepartmentId];
    private static readonly string[] Hr = ["hr"];

    private HttpStatusCode _createStatus = HttpStatusCode.Created;

    public RagToolsPageTests()
    {
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/tools/rag")
            {
                return Page(
                    new
                    {
                        id = ToolId,
                        name = "company",
                        knowledgeBases = new[] { new { id = OwnBase, name = "policies" } },
                        scope = NoScopeIds("Tenant"),
                        state = "Enabled",
                        policy = "Auto",
                        shared = false,
                        warnings = new[] { new { code = "tool.too_large", message = "The tool takes a lot of the context." } },
                    },
                    new
                    {
                        id = SharedTool,
                        name = "partner",
                        knowledgeBases = Array.Empty<object>(),
                        scope = NoScopeIds("Global"),
                        state = "Enabled",
                        policy = "Confirm",
                        shared = true,
                        warnings = Array.Empty<object>(),
                    });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases")
            {
                return Page(
                    new { id = OwnBase, name = "policies", description = "Company policies.", status = "Available", shared = false, kind = "Qdrant", scope = NoScopeIds("Tenant") },
                    new { id = OtherBase, name = "manuals", description = "Manuals.", status = "Available", shared = false, kind = "Qdrant", scope = NoScopeIds("Tenant") });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases/dictionary")
            {
                return Page(
                    new { id = DepartmentId, name = "department", type = "Text", description = (string?)null, scope = NoScopeIds("Tenant"), usedByCount = 1 },
                    new { id = YearId, name = "year", type = "Number", description = (string?)null, scope = NoScopeIds("Tenant"), usedByCount = 1 });
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{OwnBase}")
            {
                return FakeApiHandler.Json(KnowledgeBase(OwnBase, "policies", DepartmentId, "department"));
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{OtherBase}")
            {
                return FakeApiHandler.Json(KnowledgeBase(OtherBase, "manuals", YearId, "year"));
            }

            if (path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = Guid.NewGuid(), name = "Assistant" } });
            }

            if (path == "/api/tenants")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (method == HttpMethod.Get && path == $"/api/tools/rag/{ToolId}")
            {
                return FakeApiHandler.Json(Detail(ToolId, "company", OwnOnly, DepartmentOnly, new { department = Hr }));
            }

            if (method == HttpMethod.Get && path == $"/api/tools/rag/{CreatedTool}")
            {
                return FakeApiHandler.Json(Detail(CreatedTool, "support", BothBases, DepartmentOnly, new { }));
            }

            if (method == HttpMethod.Post && path == "/api/tools/rag")
            {
                return _createStatus == HttpStatusCode.Created
                    ? FakeApiHandler.Json(new { id = CreatedTool, definition = Definition("support") }, HttpStatusCode.Created)
                    : FakeApiHandler.Json(
                        new { title = "Bad request", detail = "A knowledge base of the tool is not visible to its whole scope.", code = "rag.knowledge_base_not_visible" },
                        _createStatus);
            }

            if (method == HttpMethod.Put && path.StartsWith("/api/tools/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Delete && path == $"/api/tools/rag/{ToolId}")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static object KnowledgeBase(Guid id, string name, Guid fieldId, string fieldName) => new
    {
        id,
        name,
        description = $"{name}.",
        status = "Available",
        shared = false,
        kind = "Qdrant",
        connection = new { url = "http://qdrant:6334", collection = name, vectorName = (string?)null, hasApiKey = false },
        embeddingModel = "nomic-embed-text",
        queryPrefix = (string?)null,
        mapping = new
        {
            roles = new { text = "text", documentId = (string?)null, title = (string?)null, position = (string?)null, sourceUrl = (string?)null },
            fields = new[] { new { path = fieldName, dictionaryFieldId = fieldId, name = fieldName } },
        },
        scope = NoScopeIds("Tenant"),
    };

    private static object Definition(string name) => new
    {
        name = $"rag__{name}",
        description = "Searches the company policies.",
        inputSchema = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } },
        estimatedTokens = 1250,
    };

    private static object Detail(Guid id, string name, Guid[] bases, Guid[] exposed, object fixedFilters) => new
    {
        id,
        name,
        description = "Searches the company policies.",
        knowledgeBaseIds = bases,
        exposedFields = exposed.Select(field => new { fieldId = field, alias = (string?)null }).ToArray(),
        fixedFilters,
        topK = 7,
        scoreThreshold = (double?)0.25,
        maxResultCharacters = 4000,
        scope = NoScopeIds("Tenant"),
        state = "Enabled",
        policy = "Auto",
        definition = Definition(name),
        warnings = Array.Empty<object>(),
    };

    [Fact] // AC-3
    public void TheToolsAreListedWithTheirKnowledgeBasesScopeAndWarnings()
    {
        var page = Visit<RagTools>("/tools/rag");

        var row = page.Find("[data-rag=company]").ParentElement!.ParentElement!;
        Assert.Contains("rag__company", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("policies", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Tenant", row.TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(row.QuerySelectorAll("[data-warning=\"tool.too_large\"]"));
    }

    [Fact] // AC-9
    public void ASharedToolIsReadOnlyInTheList()
    {
        var page = Visit<RagTools>("/tools/rag");

        var row = page.Find("[data-rag=partner]").ParentElement!.ParentElement!;
        Assert.Contains("Shared with you", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("managed by the tenant that owns it", row.TextContent, StringComparison.Ordinal);
        Assert.Empty(row.QuerySelectorAll("select"));
    }

    [Fact] // AC-9
    public void TheStateAndThePolicyAreChangedInTheList()
    {
        var page = Visit<RagTools>("/tools/rag");

        page.Find("[data-rag=company]").ParentElement!.ParentElement!.QuerySelector("select")!.Change(Quorum.Web.Client.Api.ToolPolicies.Confirm);

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/tools/{ToolId}")));
        var body = Body(Api, HttpMethod.Put, $"/api/tools/{ToolId}");
        Assert.Equal(("Enabled", "Confirm"), (body.GetProperty("state").GetString(), body.GetProperty("policy").GetString()));
    }

    [Fact] // AC-3
    public void TheStatusFilterIsSentToTheServer()
    {
        Visit<RagTools>("/tools/rag?status=PendingReview");

        Assert.Contains(Api.Queries, q => q.Contains("status=PendingReview", StringComparison.Ordinal));
    }

    [Fact] // AC-11
    public void AnEmptyListInvitesToAddATool()
    {
        var script = Api.Respond;
        Api.Respond = (method, path) => method == HttpMethod.Get && path == "/api/tools/rag" ? Page() : script(method, path);

        var page = Visit<RagTools>("/tools/rag");

        Assert.Contains("No rag tool is configured yet", page.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC-16
    public void EditingOpensTheSavedToolWithItsDefinition()
    {
        var page = Visit<RagTools>($"/tools/rag/{ToolId}", p => p.Add(c => c.Id, ToolId.ToString()));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=rag]")));
        Assert.Contains("rag__company", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Definition the model gets", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Delete", page.Find("[data-form=rag]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-19
    public void ANewToolNeedsAKnowledgeBaseBeforeAnythingIsSent()
    {
        var page = Visit<RagTools>("/tools/rag/new", p => p.Add(c => c.Id, "new"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=rag]")));
        page.Find("[data-form=rag] input[type=text]").Change("support");

        page.Find("[data-form=rag] form").Submit();

        page.WaitForAssertion(() => Assert.Contains("Choose at least one knowledge base.", page.Markup, StringComparison.Ordinal));
        Assert.Equal(0, Api.Requests.Count(r => r.Method == HttpMethod.Post && r.Path == "/api/tools/rag"));
    }

    [Fact] // AC-19
    public void ChoosingKnowledgeBasesAndSavingSendsTheToolAndKeepsTheDrawerOnIt()
    {
        var page = Visit<RagTools>("/tools/rag/new", p => p.Add(c => c.Id, "new"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=rag]")));
        var inputs = page.Find("[data-form=rag]").QuerySelectorAll("input[type=text], textarea");
        inputs[0].Change("support");
        inputs[1].Change("Searches the support articles.");
        page.FindAll("[data-form=rag] .mud-checkbox input")[0].Change(true);

        page.Find("[data-form=rag] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Requests.Count(r => r.Method == HttpMethod.Post && r.Path == "/api/tools/rag")));
        var body = Body(Api, HttpMethod.Post, "/api/tools/rag");
        Assert.Equal("support", body.GetProperty("name").GetString());
        Assert.Equal(OwnBase.ToString(), body.GetProperty("knowledgeBaseIds")[0].GetString());
        page.WaitForAssertion(() => Assert.Contains("was saved", page.Markup, StringComparison.Ordinal));
    }

    [Fact] // AC-19
    public void AServerRefusalIsShownAndTheFormKeepsWhatWasTyped()
    {
        _createStatus = HttpStatusCode.BadRequest;
        var page = Visit<RagTools>("/tools/rag/new", p => p.Add(c => c.Id, "new"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=rag]")));
        var inputs = page.Find("[data-form=rag]").QuerySelectorAll("input[type=text], textarea");
        inputs[0].Change("support");
        inputs[1].Change("Searches the support articles.");
        page.FindAll("[data-form=rag] .mud-checkbox input")[0].Change(true);

        page.Find("[data-form=rag] form").Submit();

        page.WaitForAssertion(() => Assert.Contains("not visible to its whole scope", page.Markup, StringComparison.Ordinal));
        Assert.Equal("support", page.Find("[data-form=rag] input[type=text]").GetAttribute("value"));
    }

    [Fact] // AC-16
    public void DeletingAsksFirstAndThenRemovesTheTool()
    {
        var page = Visit<RagTools>($"/tools/rag/{ToolId}", p => p.Add(c => c.Id, ToolId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=rag]")));

        page.FindAll("[data-form=rag] button").First(b => b.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();
        Assert.Equal(0, Api.Count(HttpMethod.Delete, "/api/tools/rag"));
        Dialogs.WaitForAssertion(() => Assert.Contains("removed from every assistant", Dialogs.Markup, StringComparison.Ordinal));
        ClickButton(Dialogs, "Delete tool");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Delete, $"/api/tools/rag/{ToolId}")));
    }
}
