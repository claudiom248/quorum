using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client.Api;
using Quorum.Web.Client.Components;

namespace Quorum.Integration.Tests.Web;

/// <summary>The rag tool editor of the Tools page: knowledge bases, exposed fields, fixed filters, limits and the definition preview (spec 003, AC-19 to AC-23, AC-49).</summary>
public sealed class RagToolsSectionTests : BunitContext
{
    private static readonly Guid DepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4001");
    private static readonly Guid YearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4002");
    private static readonly Guid OwnBase = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4011");
    private static readonly Guid OtherBase = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4012");
    private static readonly Guid SharedBase = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4013");
    private static readonly Guid ToolId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4021");
    private static readonly Guid SharedTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4022");
    private static readonly Guid CreatedTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4023");

    private static readonly Guid[] OwnOnly = [OwnBase];
    private static readonly Guid[] BothBases = [OwnBase, OtherBase];
    private static readonly Guid[] DepartmentOnly = [DepartmentId];
    private static readonly Guid[] BothFields = [DepartmentId, YearId];
    private static readonly string[] Hr = ["hr"];

    private readonly FakeApiHandler _api = new();
    private HttpStatusCode _createStatus = HttpStatusCode.Created;

    public RagToolsSectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new QuorumApi(_api.CreateClient()));

        _api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/tools/rag")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new
                    {
                        id = ToolId,
                        name = "company",
                        knowledgeBases = new[] { new { id = OwnBase, name = "policies" } },
                        scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
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
                        scope = new { kind = "Global", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
                        state = "Enabled",
                        policy = "Confirm",
                        shared = true,
                        warnings = Array.Empty<object>(),
                    },
                });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new { id = OwnBase, name = "policies", description = "Company policies.", status = "Available", shared = false, kind = "Qdrant", scope = "Tenant" },
                    new { id = OtherBase, name = "manuals", description = "Manuals.", status = "Available", shared = false, kind = "Qdrant", scope = "Tenant" },
                    new { id = SharedBase, name = "handbook", description = "Shared.", status = "Available", shared = true },
                });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases/dictionary")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new { id = DepartmentId, name = "department", type = "Text", description = (string?)null, scope = "Tenant", usedByCount = 1 },
                    new { id = YearId, name = "year", type = "Number", description = (string?)null, scope = "Tenant", usedByCount = 1 },
                });
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

            if (method == HttpMethod.Get && path == $"/api/tools/rag/{ToolId}")
            {
                return FakeApiHandler.Json(Detail(ToolId, "company", OwnOnly, DepartmentOnly, new { department = Hr }));
            }

            if (method == HttpMethod.Get && path == $"/api/tools/rag/{CreatedTool}")
            {
                return FakeApiHandler.Json(Detail(CreatedTool, "support", BothBases, BothFields, new { }));
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
        scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
    };

    private static object Definition(string name) => new
    {
        name = $"rag__{name}",
        description = "Searches the company policies.\n\nKnowledge bases:\n- policies: Company policies.",
        inputSchema = new
        {
            type = "object",
            properties = new { query = new { type = "string" }, department = new { type = "array" } },
            required = new[] { "query" },
            additionalProperties = false,
        },
        estimatedTokens = 1250,
    };

    private static object Detail(Guid id, string name, Guid[] bases, Guid[] exposed, object fixedFilters) => new
    {
        id,
        name,
        description = "Searches the company policies.",
        knowledgeBaseIds = bases,
        exposedFieldIds = exposed,
        fixedFilters,
        topK = 7,
        scoreThreshold = (double?)0.25,
        maxResultCharacters = 4000,
        scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
        state = "Enabled",
        policy = "Auto",
        definition = Definition(name),
        warnings = Array.Empty<object>(),
    };

    private static JsonElement Body(FakeApiHandler api, HttpMethod method, string path) =>
        JsonDocument.Parse(api.LastBody(method, path)!).RootElement.Clone();

    private IRenderedComponent<RagToolsSection> RenderSection()
    {
        var section = Render<RagToolsSection>();
        section.WaitForAssertion(() => Assert.DoesNotContain("mud-progress-linear", section.Markup, StringComparison.Ordinal));
        return section;
    }

    private static void Click(IRenderedComponent<RagToolsSection> section, string text) =>
        section.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    private static IElement Input(IRenderedComponent<RagToolsSection> section, string label)
    {
        var element = section.FindAll("label").First(l => l.TextContent.Replace("*", string.Empty, StringComparison.Ordinal).Trim() == label);
        return section.Find($"#{element.GetAttribute("for")}");
    }

    private static void Tick(IRenderedComponent<RagToolsSection> section, string label, bool on) =>
        section.FindAll("label").First(l => l.TextContent.Trim() == label).QuerySelector("input[type=checkbox]")!.Change(on);

    [Fact]
    public void TheRagToolsAreListedWithTheirKnowledgeBasesScopeAndWarnings()
    {
        var section = RenderSection();

        var tool = section.Find("[data-rag=company]");
        Assert.Contains("rag__company", tool.TextContent, StringComparison.Ordinal);
        Assert.Contains("policies", tool.TextContent, StringComparison.Ordinal);
        Assert.Contains("This tenant", tool.TextContent, StringComparison.Ordinal);
        Assert.Contains("takes a lot of the context", tool.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-23
    public void ASharedRagToolIsReadOnlyAndShowsNoBinding()
    {
        var section = RenderSection();

        var shared = section.Find("[data-rag=partner]");
        Assert.Contains("Shared with you", shared.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit", shared.TextContent, StringComparison.Ordinal);
        Assert.Null(shared.QuerySelector(".mud-switch input:not([disabled])"));
    }

    [Fact]
    public void ThePolicyOfARagToolIsChangedLikeThatOfAnyOtherTool()
    {
        var section = RenderSection();

        section.Find("[data-rag=company] [data-tool=company] select").Change(ToolPolicies.Confirm);

        section.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/{ToolId}")));
        var body = Body(_api, HttpMethod.Put, $"/api/tools/{ToolId}");
        Assert.Equal(("Enabled", "Confirm"), (body.GetProperty("state").GetString(), body.GetProperty("policy").GetString()));
    }

    [Fact] // AC-19, AC-49
    public void ChoosingKnowledgeBasesOffersAndExposesTheirFieldsAndPreviewsTheParameters()
    {
        var section = RenderSection();
        Click(section, "Add knowledge base tool");

        Tick(section, "policies", true);

        section.WaitForAssertion(() => Assert.Contains("Fields the model can filter on (1 of 10)", section.Markup, StringComparison.Ordinal));
        Assert.Contains("department (text)", section.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("year (number)", section.Markup, StringComparison.Ordinal);
        Assert.Equal("query, department", section.Find("[data-parameters] .tool-name").TextContent);

        Tick(section, "manuals", true);

        section.WaitForAssertion(() => Assert.Contains("Fields the model can filter on (2 of 10)", section.Markup, StringComparison.Ordinal));
        Assert.Equal("query, knowledge_base, department, year_min, year_max", section.Find("[data-parameters] .tool-name").TextContent);

        Tick(section, "manuals", false);

        section.WaitForAssertion(() => Assert.Contains("Fields the model can filter on (1 of 10)", section.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("year (number)", section.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AKnowledgeBaseSharedWithYouOffersEveryFieldAndSaysWhy()
    {
        var section = RenderSection();
        Click(section, "Add knowledge base tool");

        Tick(section, "handbook (shared with you)", true);

        section.WaitForAssertion(() => Assert.Contains("does not show its mapping", section.Markup, StringComparison.Ordinal));
        Assert.Contains("department (text)", section.Markup, StringComparison.Ordinal);
        Assert.Contains("year (number)", section.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC-19, AC-49
    public void SavingSendsTheBindingTheFixedFiltersAndTheLimitsAndThenShowsTheDefinitionAndItsSize()
    {
        var section = RenderSection();
        Click(section, "Add knowledge base tool");
        Input(section, "Name").Change("support");
        Input(section, "Description").Change("Searches the support knowledge.");
        Tick(section, "policies", true);
        Tick(section, "manuals", true);
        section.Find("select[aria-label='Add a fixed filter']").Change(DepartmentId.ToString());
        Input(section, "department is any of").Change("support, billing");
        section.Find("[data-form=rag] select[aria-label=Policy]").Change("Confirm");

        section.Find("[data-form=rag] form").Submit();

        section.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, "/api/tools/rag")));
        var body = Body(_api, HttpMethod.Post, "/api/tools/rag");
        Assert.Equal(("support", "Confirm", 5, 6000), (body.GetProperty("name").GetString(), body.GetProperty("policy").GetString(), body.GetProperty("topK").GetInt32(), body.GetProperty("maxResultCharacters").GetInt32()));
        Assert.Equal([OwnBase, OtherBase], body.GetProperty("knowledgeBaseIds").EnumerateArray().Select(v => v.GetGuid()));
        Assert.Equal([DepartmentId, YearId], body.GetProperty("exposedFieldIds").EnumerateArray().Select(v => v.GetGuid()).Order());
        Assert.Equal(["support", "billing"], body.GetProperty("fixedFilters").GetProperty("department").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("Tenant", body.GetProperty("scope").GetProperty("kind").GetString());

        section.WaitForAssertion(() => Assert.NotEmpty(section.FindAll("[data-definition]")));
        var definition = section.Find("[data-definition]");
        Assert.Contains("rag__support", definition.TextContent, StringComparison.Ordinal);
        Assert.Contains("about 1.3k tokens of the context", definition.TextContent, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\": false", definition.TextContent, StringComparison.Ordinal);
        Assert.Contains("rag__support was saved", section.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC-21
    public void TheLimitsAreCheckedBeforeAnythingIsSent()
    {
        var section = RenderSection();
        Click(section, "Add knowledge base tool");
        Input(section, "Name").Change("Bad Name");
        Tick(section, "policies", true);

        section.Find("[data-form=rag] form").Submit();

        Assert.Contains("The name is 1 to 40 characters", section.Markup, StringComparison.Ordinal);
        Assert.Contains("The description is required", section.Markup, StringComparison.Ordinal);
        Assert.Equal(0, _api.Count(HttpMethod.Post, "/api/tools/rag"));
    }

    [Fact] // AC-21
    public void NoMoreThanTenKnowledgeBasesCanBeChosen()
    {
        var section = RenderSection();
        Click(section, "Add knowledge base tool");

        Tick(section, "policies", true);

        Assert.Contains("Knowledge bases (1 of 10)", section.Markup, StringComparison.Ordinal);
    }

    [Fact] // AC-22
    public void AServerRefusalIsShownAndTheFormKeepsWhatWasTyped()
    {
        _createStatus = HttpStatusCode.BadRequest;
        var section = RenderSection();
        Click(section, "Add knowledge base tool");
        Input(section, "Name").Change("support");
        Input(section, "Description").Change("Searches the support knowledge.");
        Tick(section, "policies", true);

        section.Find("[data-form=rag] form").Submit();

        section.WaitForAssertion(() => Assert.Contains("not visible to its whole scope", section.Markup, StringComparison.Ordinal));
        Assert.Equal("support", Input(section, "Name").GetAttribute("value"));
    }

    [Fact]
    public void EditingLoadsTheSavedToolWithItsFixedFiltersAndItsDefinition()
    {
        var section = RenderSection();

        section.Find("[data-rag=company]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Edit", StringComparison.Ordinal)).Click();

        section.WaitForAssertion(() => Assert.NotEmpty(section.FindAll("[data-form=rag]")));
        Assert.Equal("company", Input(section, "Name").GetAttribute("value"));
        Assert.Equal("hr", Input(section, "department is any of").GetAttribute("value"));
        Assert.Equal("7", Input(section, "Results").GetAttribute("value"));
        Assert.Contains("about 1.3k tokens", section.Find("[data-definition]").TextContent, StringComparison.Ordinal);

        section.Find("[data-form=rag] form").Submit();

        section.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/rag/{ToolId}")));
        var body = Body(_api, HttpMethod.Put, $"/api/tools/rag/{ToolId}");
        Assert.Equal(("company", 7, 0.25, 4000), (body.GetProperty("name").GetString(), body.GetProperty("topK").GetInt32(), body.GetProperty("scoreThreshold").GetDouble(), body.GetProperty("maxResultCharacters").GetInt32()));
        Assert.Equal(["hr"], body.GetProperty("fixedFilters").GetProperty("department").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void AFixedFilterWithoutAValueIsRefusedInTheForm()
    {
        var section = RenderSection();
        Click(section, "Add knowledge base tool");
        Input(section, "Name").Change("support");
        Input(section, "Description").Change("Searches the support knowledge.");
        Tick(section, "policies", true);
        section.Find("select[aria-label='Add a fixed filter']").Change(DepartmentId.ToString());

        section.Find("[data-form=rag] form").Submit();

        Assert.Contains("The fixed filter on department has no value", section.Markup, StringComparison.Ordinal);
        Assert.Equal(0, _api.Count(HttpMethod.Post, "/api/tools/rag"));
    }

    [Fact]
    public void DeletingARagToolAsksFirst()
    {
        var section = RenderSection();

        section.Find("[data-rag=company]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();
        Assert.Equal(0, _api.Count(HttpMethod.Delete, "/api/tools/rag/"));
        section.Find("[data-rag=company]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Delete tool", StringComparison.Ordinal)).Click();

        section.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Delete, $"/api/tools/rag/{ToolId}")));
        section.WaitForAssertion(() => Assert.Contains("rag__company was deleted", section.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutKnowledgeBasesTheEditorPointsToTheKnowledgePage()
    {
        var script = _api.Respond;
        _api.Respond = (method, path) =>
            method == HttpMethod.Get && path == "/api/knowledge-bases"
                ? FakeApiHandler.Json(Array.Empty<object>())
                : script(method, path);
        var section = RenderSection();

        Click(section, "Add knowledge base tool");

        Assert.Contains("No knowledge base is registered yet", section.Markup, StringComparison.Ordinal);
        Assert.Equal("knowledge", section.Find("[data-form=rag] a[href]").GetAttribute("href"));
    }
}
