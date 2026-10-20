using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client.Api;
using DictionaryPage = Quorum.Web.Client.Pages.KnowledgeDictionary;
using KnowledgePage = Quorum.Web.Client.Pages.Knowledge;

namespace Quorum.Integration.Tests.Web;

/// <summary>The admin pages for the dictionary and the knowledge bases, with the guided registration (spec 003, AC-49).</summary>
public sealed class KnowledgePagesTests : BunitContext
{
    private static readonly Guid DepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3001");
    private static readonly Guid YearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3002");
    private static readonly Guid OwnId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3011");
    private static readonly Guid BrokenId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3012");
    private static readonly Guid SharedId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3013");
    private static readonly Guid AssistantId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3021");

    private readonly FakeApiHandler _api = new();
    private bool _inspectReachable = true;
    private HttpStatusCode _deleteFieldStatus = HttpStatusCode.NoContent;
    private string _savedStatus = "Available";

    public KnowledgePagesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new QuorumApi(_api.CreateClient()));

        _api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/knowledge-bases/dictionary")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new { id = DepartmentId, name = "department", type = "Text", description = "The owning department.", scope = "Tenant", usedByCount = 1 },
                    new { id = YearId, name = "year", type = "Number", description = (string?)null, scope = "Tenant", usedByCount = 0 },
                });
            }

            if (method == HttpMethod.Post && path == "/api/knowledge-bases/dictionary")
            {
                return FakeApiHandler.Json(new { id = Guid.NewGuid() }, HttpStatusCode.Created);
            }

            if (method == HttpMethod.Put && path.StartsWith("/api/knowledge-bases/dictionary/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Delete && path.StartsWith("/api/knowledge-bases/dictionary/", StringComparison.Ordinal))
            {
                return _deleteFieldStatus == HttpStatusCode.NoContent
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : FakeApiHandler.Json(
                        new { title = "Conflict", detail = "The field is used by knowledge bases policies: remove it from their mapping first.", code = "knowledge_base.field_in_use" },
                        _deleteFieldStatus);
            }

            if (path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = AssistantId, name = "Assistant" } });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new { id = OwnId, name = "policies", description = "Company policies.", status = "Available", shared = false, kind = "Qdrant", scope = "Tenant", lastVerifiedAt = (DateTimeOffset?)DateTimeOffset.UtcNow, warning = (string?)null },
                    new { id = BrokenId, name = "manuals", description = "Product manuals.", status = "Unavailable", shared = false, kind = "Qdrant", scope = "Global", lastVerifiedAt = (DateTimeOffset?)null, warning = "The collection manuals was not found on the server." },
                    new { id = SharedId, name = "handbook", description = "Shared handbook.", status = "Available", shared = true },
                });
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{OwnId}")
            {
                return FakeApiHandler.Json(new
                {
                    id = OwnId,
                    name = "policies",
                    description = "Company policies.",
                    status = "Available",
                    shared = false,
                    kind = "Qdrant",
                    connection = new { url = "http://qdrant:6334", collection = "docs", vectorName = (string?)null, hasApiKey = true },
                    embeddingModel = "nomic-embed-text",
                    queryPrefix = (string?)null,
                    mapping = new
                    {
                        roles = new { text = "text", documentId = (string?)null, title = "title", position = (string?)null, sourceUrl = (string?)null },
                        fields = new[] { new { path = "department", dictionaryFieldId = DepartmentId, name = "department" } },
                    },
                    scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
                });
            }

            if (method == HttpMethod.Post && path == "/api/knowledge-bases/inspect")
            {
                return FakeApiHandler.Json(Inspection());
            }

            if (method == HttpMethod.Post && path == "/api/knowledge-bases")
            {
                return FakeApiHandler.Json(new { id = Guid.NewGuid(), status = _savedStatus, warnings = Warnings(_savedStatus) }, HttpStatusCode.Created);
            }

            if (method == HttpMethod.Put && path == $"/api/knowledge-bases/{OwnId}")
            {
                return FakeApiHandler.Json(new { status = "Available", warnings = Array.Empty<object>() });
            }

            if (method == HttpMethod.Post && path.EndsWith("/verify", StringComparison.Ordinal))
            {
                return FakeApiHandler.Json(new
                {
                    status = "Unavailable",
                    warnings = new[] { new { code = "knowledge_base.qdrant_unreachable", message = "The server could not be reached.", path = (string?)null } },
                });
            }

            if (method == HttpMethod.Post && path.EndsWith("/search", StringComparison.Ordinal))
            {
                return FakeApiHandler.Json(new
                {
                    results = new object[]
                    {
                        new { score = 0.8123, text = "Employees get 25 days of leave.", source = new { knowledgeBase = "policies", documentId = "d1", title = "Leave policy", position = "p. 3", url = "https://example.com/leave" } },
                        new { score = 0.4, text = "Unsafe link.", source = new { knowledgeBase = "policies", documentId = "d2", title = "Other", position = (string?)null, url = "javascript:alert(1)" } },
                    },
                    notes = Array.Empty<string>(),
                });
            }

            if (method == HttpMethod.Delete && path.StartsWith("/api/knowledge-bases/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static object[] Warnings(string status) => status == "Available"
        ? []
        : [new { code = "knowledge_base.qdrant_unreachable", message = "The server could not be reached.", path = (string?)null }];

    private object Inspection() => new
    {
        reachable = _inspectReachable,
        collectionExists = _inspectReachable,
        vectors = new[] { new { name = (string?)null, size = 64, distance = "Cosine" } },
        embeddingSize = 64,
        dimensionMatch = true,
        draftMapping = new
        {
            roles = new { text = "chunk_text", documentId = "doc_id", title = "file_name", position = "page", sourceUrl = "source_url" },
            fields = new object[]
            {
                new { path = "department", name = "department", type = "Text", dictionaryFieldId = (Guid?)DepartmentId, issues = Array.Empty<string>() },
                new { path = "meta.reviewed", name = "reviewed", type = "Boolean", dictionaryFieldId = (Guid?)null, issues = Array.Empty<string>() },
            },
        },
        proposedFields = new[] { new { name = "reviewed", type = "Boolean" } },
        unindexedFields = new[] { "meta.reviewed" },
        warnings = _inspectReachable
            ? new[] { new { code = "knowledge_base.field_unindexed", message = "meta.reviewed has no payload index.", path = (string?)"meta.reviewed" } }
            : [new { code = "knowledge_base.qdrant_unreachable", message = "The server could not be reached.", path = (string?)null }],
    };

    private int Registrations() => _api.Requests.Count(r => r.Method == HttpMethod.Post && r.Path == "/api/knowledge-bases");

    private static JsonElement Body(FakeApiHandler api, HttpMethod method, string path) =>
        JsonDocument.Parse(api.LastBody(method, path)!).RootElement.Clone();

    private IRenderedComponent<TPage> RenderPage<TPage>()
        where TPage : IComponent
    {
        var page = Render<TPage>();
        page.WaitForAssertion(() => Assert.DoesNotContain("mud-progress-linear", page.Markup, StringComparison.Ordinal));
        return page;
    }

    private static void Click(IRenderedComponent<KnowledgePage> page, string text) =>
        page.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    private static void ClickIn(IRenderedComponent<KnowledgePage> page, string selector, string text) =>
        page.Find(selector).QuerySelectorAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    // MudBlazor links a label to its input with `for`: that is how a field is found by what the admin reads.
    private static IElement Input<TPage>(IRenderedComponent<TPage> page, string label)
        where TPage : IComponent
    {
        var element = page.FindAll("label").First(l => l.TextContent.Replace("*", string.Empty, StringComparison.Ordinal).Trim() == label);
        return page.Find($"#{element.GetAttribute("for")}");
    }

    // --- Dictionary ---

    [Fact] // AC-1, AC-4
    public void TheDictionaryListsFieldsWithTheirTypeAndUsage()
    {
        var page = RenderPage<DictionaryPage>();

        var department = page.Find("[data-field=department]");
        Assert.Contains("Text", department.TextContent, StringComparison.Ordinal);
        Assert.Contains("Used by 1", department.TextContent, StringComparison.Ordinal);
        Assert.Contains("The owning department.", department.TextContent, StringComparison.Ordinal);
        Assert.Contains("Used by 0", page.Find("[data-field=year]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-1
    public void AddingAFieldSendsNameTypeScopeAndDescription()
    {
        var page = RenderPage<DictionaryPage>();
        page.FindAll("button").First(b => b.TextContent.Contains("Add field", StringComparison.Ordinal)).Click();

        Input(page, "Name").Change("region");
        page.Find("[data-form=field] select[aria-label=Type]").Change("TextList");
        page.Find("[data-form=field] select[aria-label=Scope]").Change("Global");
        Input(page, "Description").Change("Sales region");
        page.Find("[data-form=field] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, "/api/knowledge-bases/dictionary")));
        var body = Body(_api, HttpMethod.Post, "/api/knowledge-bases/dictionary");
        Assert.Equal(
            ("region", "TextList", "Global", "Sales region"),
            (body.GetProperty("name").GetString(), body.GetProperty("type").GetString(), body.GetProperty("scope").GetString(), body.GetProperty("description").GetString()));
        page.WaitForAssertion(() => Assert.Contains("region was added", page.Markup, StringComparison.Ordinal));
    }

    [Fact] // AC-4
    public void OnlyTheDescriptionOfAFieldCanBeEdited()
    {
        var page = RenderPage<DictionaryPage>();

        page.Find("[data-field=year]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Edit description", StringComparison.Ordinal)).Click();
        Input(page, "Description").Change("The year it was approved.");
        page.Find("[data-field=year] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/knowledge-bases/dictionary/{YearId}")));
        var body = Body(_api, HttpMethod.Put, $"/api/knowledge-bases/dictionary/{YearId}");
        Assert.Equal("The year it was approved.", body.GetProperty("description").GetString());
        Assert.Single(body.EnumerateObject());
    }

    [Fact] // AC-4
    public void AFieldInUseCannotBeDeletedAndTheServerSaysWhoUsesIt()
    {
        _deleteFieldStatus = HttpStatusCode.Conflict;
        var page = RenderPage<DictionaryPage>();

        page.Find("[data-field=department]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();
        page.Find("[data-field=department]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Delete field", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Contains("used by knowledge bases policies", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnusedFieldIsDeletedAfterAConfirmation()
    {
        var page = RenderPage<DictionaryPage>();

        page.Find("[data-field=year]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();
        Assert.Equal(0, _api.Count(HttpMethod.Delete, "/api/knowledge-bases/dictionary/"));
        page.Find("[data-field=year]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Delete field", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Delete, $"/api/knowledge-bases/dictionary/{YearId}")));
        page.WaitForAssertion(() => Assert.Contains("The field year was deleted", page.Markup, StringComparison.Ordinal));
    }

    // --- Knowledge bases ---

    [Fact]
    public void TheKnowledgeBasesAreListedWithTheirStatusAndWhyOneIsUnavailable()
    {
        var page = RenderPage<KnowledgePage>();

        Assert.Contains("Available", page.Find("[data-kb=policies]").TextContent, StringComparison.Ordinal);
        var broken = page.Find("[data-kb=manuals]");
        Assert.Contains("Unavailable", broken.TextContent, StringComparison.Ordinal);
        Assert.Contains("was not found on the server", broken.TextContent, StringComparison.Ordinal);
        Assert.Contains("every tenant", broken.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-43
    public void AKnowledgeBaseOfAnotherTenantShowsNoActionsAndNoConnection()
    {
        var page = RenderPage<KnowledgePage>();

        var shared = page.Find("[data-kb=handbook]");
        Assert.Contains("Shared with you", shared.TextContent, StringComparison.Ordinal);
        Assert.Empty(shared.QuerySelectorAll("button"));
    }

    [Fact] // AC-17
    public void VerifyingShowsTheOutcomeAndItsWarnings()
    {
        var page = RenderPage<KnowledgePage>();

        ClickIn(page, "[data-kb=manuals]", "Verify");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, $"/api/knowledge-bases/{BrokenId}/verify")));
        page.WaitForAssertion(() => Assert.Contains("The server could not be reached.", page.Find("[data-kb=manuals]").TextContent, StringComparison.Ordinal));
    }

    [Fact] // AC-48
    public void DeletingARegistrationAsksFirstAndSaysTheStoreIsUntouched()
    {
        var page = RenderPage<KnowledgePage>();

        ClickIn(page, "[data-kb=policies]", "Delete");
        Assert.Contains("The data in the store is not touched", page.Find("[data-kb=policies]").TextContent, StringComparison.Ordinal);
        Assert.Equal(0, _api.Count(HttpMethod.Delete, $"/api/knowledge-bases/{OwnId}"));
        ClickIn(page, "[data-kb=policies]", "Delete registration");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Delete, $"/api/knowledge-bases/{OwnId}")));
    }

    [Fact] // AC-47, AC-40
    public void ATestSearchShowsScoredResultsAndLinksOnlyHttpAndHttps()
    {
        var page = RenderPage<KnowledgePage>();

        ClickIn(page, "[data-kb=policies]", "Test search");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=test-search]")));
        Input(page, "Question").Change("how many days of leave");
        page.Find("[data-form=test-search] select[aria-label='Number of results']").Change("10");
        page.Find("[data-form=test-search] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, $"/api/knowledge-bases/{OwnId}/search")));
        var body = Body(_api, HttpMethod.Post, $"/api/knowledge-bases/{OwnId}/search");
        Assert.Equal(("how many days of leave", 10), (body.GetProperty("query").GetString(), body.GetProperty("limit").GetInt32()));

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".search-hit").Count));
        var first = page.FindAll(".search-hit")[0];
        Assert.Contains("0.812", first.TextContent, StringComparison.Ordinal);
        Assert.Contains("Leave policy (p. 3)", first.TextContent, StringComparison.Ordinal);
        Assert.Equal("noopener noreferrer", first.QuerySelector("a")!.GetAttribute("rel"));
        Assert.Null(page.FindAll(".search-hit")[1].QuerySelector("a"));
    }

    [Fact]
    public void ATestSearchCanFilterOnTheMappedFields()
    {
        var page = RenderPage<KnowledgePage>();
        ClickIn(page, "[data-kb=policies]", "Test search");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=test-search]")));

        Input(page, "Question").Change("leave");
        Input(page, "department is any of").Change("hr, legal");
        page.Find("[data-form=test-search] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, $"/api/knowledge-bases/{OwnId}/search")));
        var filters = Body(_api, HttpMethod.Post, $"/api/knowledge-bases/{OwnId}/search").GetProperty("filters");
        Assert.Equal(["hr", "legal"], filters.GetProperty("department").EnumerateArray().Select(v => v.GetString()));
    }

    // --- Guided registration (AC-49) ---

    private static void FillConnection(IRenderedComponent<KnowledgePage> page)
    {
        Click(page, "Register knowledge base");
        Input(page, "Name").Change("tickets");
        Input(page, "Description").Change("Support tickets.");
        Input(page, "Qdrant address (gRPC)").Change("http://qdrant:6334");
        Input(page, "Collection").Change("tickets");
        Input(page, "Embedding model").Change("nomic-embed-text");
        Input(page, "Query prefix").Change("search_query: ");
    }

    private static void InspectAndReview(IRenderedComponent<KnowledgePage> page)
    {
        FillConnection(page);
        Click(page, "Inspect collection");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=inspection]")));
        Click(page, "Review the mapping");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=mapping]")));
    }

    [Fact]
    public void TheConnectionStepNamesWhatIsMissingAndDoesNotInspect()
    {
        var page = RenderPage<KnowledgePage>();
        Click(page, "Register knowledge base");

        Click(page, "Inspect collection");

        Assert.Contains("The collection is required", page.Markup, StringComparison.Ordinal);
        Assert.Equal(0, _api.Count(HttpMethod.Post, "/api/knowledge-bases/inspect"));
    }

    [Fact] // AC-6, AC-14
    public void InspectingSendsTheConnectionAndShowsTheFindingsTheWarningsAndTheUnindexedFields()
    {
        var page = RenderPage<KnowledgePage>();
        FillConnection(page);

        Click(page, "Inspect collection");

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=inspection]")));
        var body = Body(_api, HttpMethod.Post, "/api/knowledge-bases/inspect");
        Assert.Equal(("Qdrant", "nomic-embed-text", "search_query: "), (body.GetProperty("kind").GetString(), body.GetProperty("embeddingModel").GetString(), body.GetProperty("queryPrefix").GetString()));
        Assert.Equal(("http://qdrant:6334", "tickets"), (body.GetProperty("connection").GetProperty("url").GetString(), body.GetProperty("connection").GetProperty("collection").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("schema").ValueKind);

        var step = page.Find("[data-step=inspection]");
        Assert.Contains("The collection exists.", step.TextContent, StringComparison.Ordinal);
        Assert.Contains("they match", step.TextContent, StringComparison.Ordinal);
        Assert.Contains("meta.reviewed has no payload index", step.TextContent, StringComparison.Ordinal);
        Assert.NotNull(step.QuerySelector("[data-unindexed]"));
    }

    [Fact] // AC-8
    public void APastedSchemaIsSentAsTextOnInspectingAgain()
    {
        var page = RenderPage<KnowledgePage>();
        FillConnection(page);
        Click(page, "Inspect collection");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=inspection]")));

        Input(page, "Payload schema (optional)").Change("""{"type":"object","properties":{"text":{"type":"string"}}}""");
        Click(page, "Inspect with this schema");

        page.WaitForAssertion(() => Assert.Equal(2, _api.Count(HttpMethod.Post, "/api/knowledge-bases/inspect")));
        Assert.Equal(
            """{"type":"object","properties":{"text":{"type":"string"}}}""",
            Body(_api, HttpMethod.Post, "/api/knowledge-bases/inspect").GetProperty("schema").GetString());
    }

    [Fact] // AC-14
    public void AnUnreachableStoreShowsTheErrorAndDoesNotOfferTheMapping()
    {
        _inspectReachable = false;
        var page = RenderPage<KnowledgePage>();
        FillConnection(page);

        Click(page, "Inspect collection");

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=inspection]")));
        Assert.Contains("The server could not be reached.", page.Find("[data-step=inspection]").TextContent, StringComparison.Ordinal);
        Assert.True(page.FindAll("button").First(b => b.TextContent.Contains("Review the mapping", StringComparison.Ordinal)).HasAttribute("disabled"));
    }

    [Fact] // AC-7, AC-10
    public void TheMappingStartsFromTheDraftAndANewFieldNeedsConfirmationBeforeMovingOn()
    {
        var page = RenderPage<KnowledgePage>();
        InspectAndReview(page);

        Assert.Equal("chunk_text", Input(page, "Text (required)").GetAttribute("value"));
        Assert.Equal("doc_id", Input(page, "Document id").GetAttribute("value"));
        Assert.Equal(2, page.FindAll(".mapping-row").Count);
        Assert.Contains("Existing field", page.Find("[data-path=department]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(page.Find("[data-new-fields]"));

        Click(page, "Next");

        Assert.Contains("Confirm the creation", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-step=scope]"));
    }

    [Fact] // AC-9
    public void AClashingNameIsFlaggedOnTheRowUntilItIsChanged()
    {
        var page = RenderPage<KnowledgePage>();
        InspectAndReview(page);

        var path = page.Find("[data-path='meta.reviewed']");
        var names = path.QuerySelectorAll("input[type=text]");
        names[1].Input("department");

        page.WaitForAssertion(() => Assert.Contains("also uses the name", page.Markup, StringComparison.Ordinal));

        page.Find("[data-path='meta.reviewed']").QuerySelectorAll("input[type=text]")[1].Input("reviewed");

        page.WaitForAssertion(() => Assert.DoesNotContain("also uses the name", page.Markup, StringComparison.Ordinal));
    }

    [Fact] // AC-49
    public void RegisteringWalksThroughEveryStepAndSendsTheMappingTheScopeAndTheFieldsToCreate()
    {
        var page = RenderPage<KnowledgePage>();
        InspectAndReview(page);

        page.Find("[data-new-fields] input[type=checkbox]").Change(true);
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=scope]")));
        page.Find("[data-step=scope] select[aria-label=Scope]").Change("Assistant");
        page.Find("[data-step=scope]").QuerySelector("input[type=checkbox]")!.Change(true);
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=review]")));
        Assert.Contains("Assistants: Assistant", page.Find("[data-step=review]").TextContent, StringComparison.Ordinal);
        Assert.Contains("New dictionary field(s): reviewed", page.Find("[data-step=review]").TextContent, StringComparison.Ordinal);

        Click(page, "Register");

        page.WaitForAssertion(() => Assert.Equal(1, Registrations()));
        var body = Body(_api, HttpMethod.Post, "/api/knowledge-bases");
        Assert.Equal(("tickets", "Support tickets."), (body.GetProperty("name").GetString(), body.GetProperty("description").GetString()));
        Assert.Equal("chunk_text", body.GetProperty("mapping").GetProperty("roles").GetProperty("text").GetString());
        var fields = body.GetProperty("mapping").GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(DepartmentId, fields[0].GetProperty("dictionaryFieldId").GetGuid());
        Assert.Equal("reviewed", fields[1].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, fields[1].GetProperty("dictionaryFieldId").ValueKind);
        var created = Assert.Single(body.GetProperty("createFields").EnumerateArray());
        Assert.Equal(("reviewed", "Boolean"), (created.GetProperty("name").GetString(), created.GetProperty("type").GetString()));
        var scope = body.GetProperty("scope");
        Assert.Equal(("Assistant", AssistantId), (scope.GetProperty("kind").GetString(), scope.GetProperty("assistantIds")[0].GetGuid()));
        Assert.Equal("search_query: ", body.GetProperty("queryPrefix").GetString());

        page.WaitForAssertion(() => Assert.Contains("tickets was saved and is available", page.Markup, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-form=knowledge-base]"));
    }

    [Fact] // AC-14
    public void AKnowledgeBaseSavedUnavailableSaysSoAndShowsTheWarning()
    {
        _savedStatus = "Unavailable";
        var page = RenderPage<KnowledgePage>();
        InspectAndReview(page);
        page.Find("[data-new-fields] input[type=checkbox]").Change(true);
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=scope]")));
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=review]")));

        Click(page, "Register");

        page.WaitForAssertion(() => Assert.Contains("tickets was saved but is unavailable", page.Markup, StringComparison.Ordinal));
        Assert.Contains("The server could not be reached.", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingStartsFromTheSavedMappingAndSavesWithAPut()
    {
        var page = RenderPage<KnowledgePage>();

        ClickIn(page, "[data-kb=policies]", "Edit");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=knowledge-base]")));
        Assert.Equal("policies", Input(page, "Name").GetAttribute("value"));
        Assert.Contains("A key is stored", page.Markup, StringComparison.Ordinal);

        Click(page, "Keep the mapping");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=mapping]")));
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=scope]")));
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=review]")));
        Click(page, "Save changes");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/knowledge-bases/{OwnId}")));
        var body = Body(_api, HttpMethod.Put, $"/api/knowledge-bases/{OwnId}");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("connection").GetProperty("apiKey").ValueKind);
        Assert.Empty(body.GetProperty("createFields").EnumerateArray());
    }
}
