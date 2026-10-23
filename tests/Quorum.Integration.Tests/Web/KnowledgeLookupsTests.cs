using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client.Api;
using KnowledgePage = Quorum.Web.Client.Pages.Knowledge;
using LookupsPage = Quorum.Web.Client.Pages.KnowledgeLookups;

namespace Quorum.Integration.Tests.Web;

/// <summary>The lookups step of the registration and the Lookups page (spec 007 AC-43, admin side).</summary>
public sealed class KnowledgeLookupsTests : BunitContext
{
    private static readonly Guid DepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4001");
    private static readonly Guid YearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4002");
    private static readonly Guid OwnId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4011");
    private static readonly Guid SharedId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4013");
    private static readonly Guid ComputedLookupId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4021");
    private static readonly Guid ManualId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4031");
    private static readonly Guid SharedManualId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4032");

    private static readonly string[] ItalianSynonyms = ["Italy", "Italia"];

    private readonly FakeApiHandler _api = new();
    private HttpStatusCode _refreshStatus = HttpStatusCode.Accepted;
    private HttpStatusCode _manualSaveStatus = HttpStatusCode.Created;
    private bool _failed;

    public KnowledgeLookupsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new QuorumApi(_api.CreateClient()));

        _api.Respond = (method, path) =>
        {
            var route = path.Split('?')[0];
            if (method == HttpMethod.Get && route == "/api/knowledge-bases/dictionary")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new { id = DepartmentId, name = "department", type = "Text", description = (string?)null, scope = "Tenant", usedByCount = 1 },
                    new { id = YearId, name = "year", type = "Number", description = (string?)null, scope = "Tenant", usedByCount = 1 },
                });
            }

            if (method == HttpMethod.Get && route == "/api/knowledge-bases")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new { id = OwnId, name = "policies", description = "Company policies.", status = "Available", shared = false, kind = "Qdrant", scope = "Tenant", lastVerifiedAt = (DateTimeOffset?)DateTimeOffset.UtcNow, warning = (string?)null },
                    new { id = SharedId, name = "handbook", description = "Shared handbook.", status = "Available", shared = true },
                });
            }

            if (method == HttpMethod.Get && route == $"/api/knowledge-bases/{OwnId}")
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
                        roles = new { text = "text", documentId = "doc_id", title = (string?)null, position = (string?)null, sourceUrl = (string?)null },
                        fields = new object[]
                        {
                            new { path = "department", dictionaryFieldId = DepartmentId, name = "department", cluster = (string?)null, listable = true, entityAttribute = true },
                            new { path = "year", dictionaryFieldId = YearId, name = "year", cluster = (string?)null, listable = false, entityAttribute = false },
                        },
                        countedPoint = new { path = "chunk_index", value = 0 },
                        entityName = "title",
                    },
                    scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
                    lookupRefreshHours = 48,
                });
            }

            if (method == HttpMethod.Put && route == $"/api/knowledge-bases/{OwnId}")
            {
                return FakeApiHandler.Json(new { status = "Available", warnings = Array.Empty<object>() });
            }

            if (method == HttpMethod.Get && route == $"/api/knowledge-bases/{OwnId}/lookups")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new
                    {
                        id = ComputedLookupId,
                        name = "department",
                        kind = "Computed",
                        field = "department",
                        unit = "documents",
                        slices = new[]
                        {
                            new
                            {
                                hash = "all",
                                filters = new { },
                                status = _failed ? "Failed" : "Ready",
                                stale = _failed,
                                valueCount = 12,
                                truncated = false,
                                computedAt = (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(-3),
                                nextRefreshAt = (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(21),
                                error = _failed ? "The field department needs a payload index." : null,
                                running = false,
                            },
                        },
                    },
                });
            }

            if (method == HttpMethod.Post && (route == $"/api/knowledge-bases/{OwnId}/lookups/refresh" || route == $"/api/knowledge-bases/lookups/{ComputedLookupId}/refresh"))
            {
                return _refreshStatus == HttpStatusCode.Accepted
                    ? new HttpResponseMessage(HttpStatusCode.Accepted)
                    : FakeApiHandler.Json(new { title = "Conflict", detail = "The lookups of this knowledge base are being computed. Try again when it ends.", code = "lookup.refresh_running" }, _refreshStatus);
            }

            if (method == HttpMethod.Get && route == $"/api/knowledge-bases/lookups/{ComputedLookupId}/values")
            {
                return FakeApiHandler.Json(new
                {
                    unit = "documents",
                    asOf = (DateTimeOffset?)DateTimeOffset.UtcNow,
                    stale = false,
                    truncated = false,
                    values = new object[] { new { value = "hr", count = 8 }, new { value = "legal", count = 4 } },
                    cursor = (string?)null,
                    cursorRestarted = false,
                });
            }

            if (method == HttpMethod.Get && route == "/api/knowledge-bases/manual-lookups")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new { id = ManualId, name = "nationality", description = "Nationalities.", scope = "Tenant", valueCount = 2, shared = false, scopeTenantIds = Array.Empty<Guid>() },
                    new { id = SharedManualId, name = "audience", description = (string?)null, scope = "Global", valueCount = 1, shared = true },
                });
            }

            if (method == HttpMethod.Get && route == $"/api/knowledge-bases/lookups/{ManualId}/values")
            {
                return FakeApiHandler.Json(new
                {
                    unit = (string?)null,
                    asOf = (DateTimeOffset?)DateTimeOffset.UtcNow,
                    stale = false,
                    truncated = false,
                    values = new object[]
                    {
                        new { value = "IT", label = "Italian", synonyms = ItalianSynonyms },
                        new { value = "FR", label = "French" },
                    },
                    cursor = (string?)null,
                    cursorRestarted = false,
                });
            }

            if (method == HttpMethod.Post && route == "/api/knowledge-bases/manual-lookups")
            {
                return _manualSaveStatus == HttpStatusCode.Created
                    ? FakeApiHandler.Json(new { id = Guid.NewGuid() }, HttpStatusCode.Created)
                    : FakeApiHandler.Json(new { title = "Conflict", detail = "A lookup with this name already exists.", code = "lookup.name_taken" }, _manualSaveStatus);
            }

            if (method == HttpMethod.Put && route == $"/api/knowledge-bases/manual-lookups/{ManualId}")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Delete && route == $"/api/knowledge-bases/manual-lookups/{ManualId}")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (route == "/api/assistants")
            {
                return FakeApiHandler.Json(Array.Empty<object>());
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    // --- The lookups step of the registration ---

    [Fact]
    public void EditingKeepsTheLookupsOfTheSavedMappingAndSendsThemBack()
    {
        var page = RenderPage<KnowledgePage>();
        ClickIn(page, "[data-kb=policies]", "Edit");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=knowledge-base]")));
        Click(page, "Keep the mapping");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=mapping]")));
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=lookups]")));

        var step = page.Find("[data-step=lookups]");
        Assert.Equal("chunk_index", Input(page, "Counted point: payload path").GetAttribute("value"));
        Assert.Equal("0", Input(page, "Counted point: value").GetAttribute("value"));
        Assert.Equal("title", Input(page, "Entity name: payload path").GetAttribute("value"));
        Assert.Equal("48", Input(page, "Refresh every (hours)").GetAttribute("value"));
        Assert.True(step.QuerySelector("[data-lookup-field=department] input[type=checkbox]")!.HasAttribute("checked"));
        Assert.False(step.QuerySelector("[data-lookup-field=year] input[type=checkbox]")!.HasAttribute("checked"));

        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=scope]")));
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=review]")));
        Assert.Contains("Lookups: 1 listed field(s), entities named by title, counting the points where chunk_index is 0, refreshed every 48 hour(s)", page.Markup, StringComparison.Ordinal);
        Click(page, "Save changes");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/knowledge-bases/{OwnId}")));
        var body = Body(HttpMethod.Put, $"/api/knowledge-bases/{OwnId}");
        var mapping = body.GetProperty("mapping");
        Assert.Equal(("chunk_index", 0), (mapping.GetProperty("countedPoint").GetProperty("path").GetString(), mapping.GetProperty("countedPoint").GetProperty("value").GetInt32()));
        Assert.Equal("title", mapping.GetProperty("entityName").GetString());
        var fields = mapping.GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal((true, true), (fields[0].GetProperty("listable").GetBoolean(), fields[0].GetProperty("entityAttribute").GetBoolean()));
        Assert.Equal((false, false), (fields[1].GetProperty("listable").GetBoolean(), fields[1].GetProperty("entityAttribute").GetBoolean()));
        Assert.Equal(48, body.GetProperty("lookupRefreshHours").GetInt32());
    }

    [Fact]
    public void AListedFieldThatIsNotTextBlocksTheStepAndNamesTheField()
    {
        var page = RenderPage<KnowledgePage>();
        ClickIn(page, "[data-kb=policies]", "Edit");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=knowledge-base]")));
        Click(page, "Keep the mapping");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=mapping]")));
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=lookups]")));

        page.Find("[data-lookup-field=year] input[type=checkbox]").Change(true);
        Click(page, "Next");

        page.WaitForAssertion(() => Assert.Contains("only text and list of texts fields can be listed", page.Find("[data-step=lookups]").TextContent, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-step=scope]"));
    }

    [Fact]
    public void ACountedPointWithoutAValueBlocksTheStep()
    {
        var page = RenderPage<KnowledgePage>();
        ClickIn(page, "[data-kb=policies]", "Edit");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=knowledge-base]")));
        Click(page, "Keep the mapping");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=mapping]")));
        Click(page, "Next");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-step=lookups]")));

        Input(page, "Counted point: value").Input(string.Empty);
        Click(page, "Next");

        page.WaitForAssertion(() => Assert.Contains("needs both the payload path and the value", page.Find("[data-step=lookups]").TextContent, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-step=scope]"));
    }

    // --- The Lookups page ---

    [Fact]
    public void ThePageListsTheLookupsOfEachKnowledgeBaseWithTheirState()
    {
        var page = RenderPage<LookupsPage>();

        var lookup = page.Find("[data-lookup-kb=policies] [data-lookup=department]");
        Assert.Contains("Counts documents", lookup.TextContent, StringComparison.Ordinal);
        Assert.Contains("Ready", lookup.TextContent, StringComparison.Ordinal);
        Assert.Contains("12 values", lookup.TextContent, StringComparison.Ordinal);
        Assert.Contains("Whole knowledge base", lookup.TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-lookup-kb=handbook]"));
        Assert.Empty(page.FindAll("[data-lookup-error]"));
    }

    [Fact]
    public void AFailedComputationShowsItsErrorAndSaysModelsKeepTheLastGoodList()
    {
        _failed = true;

        var page = RenderPage<LookupsPage>();

        var error = page.Find("[data-lookup-error]");
        Assert.Contains("needs a payload index", error.TextContent, StringComparison.Ordinal);
        Assert.Contains("still read the last good list", error.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshNowAsksForTheKnowledgeBaseAndSaysItRunsInTheBackground()
    {
        var page = RenderPage<LookupsPage>();

        ClickIn(page, "[data-lookup-kb=policies]", "Refresh now");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, $"/api/knowledge-bases/{OwnId}/lookups/refresh")));
        page.WaitForAssertion(() => Assert.Contains("will be computed again within a minute", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshingOneLookupAsksForThatLookup()
    {
        var page = RenderPage<LookupsPage>();

        ClickIn(page, "[data-lookup=department] .tool-row-head", "Refresh");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, $"/api/knowledge-bases/lookups/{ComputedLookupId}/refresh")));
    }

    [Fact]
    public void ARefreshWhileAComputationRunsShowsTheConflict()
    {
        _refreshStatus = HttpStatusCode.Conflict;
        var page = RenderPage<LookupsPage>();

        ClickIn(page, "[data-lookup-kb=policies]", "Refresh now");

        page.WaitForAssertion(() => Assert.Contains("are being computed", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("will be computed again", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValuesOfALookupAreShownWithTheirCountsAndSearchedOnTheServer()
    {
        var page = RenderPage<LookupsPage>();

        ClickIn(page, "[data-lookup=department]", "Values");
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-lookup-values] tbody tr").Count));

        var header = page.Find("[data-lookup-header]").TextContent;
        Assert.Contains("documents", header, StringComparison.Ordinal);
        Assert.Contains("hr", page.Find("[data-value=hr]").TextContent, StringComparison.Ordinal);
        Assert.Contains("8", page.Find("[data-value=hr]").TextContent, StringComparison.Ordinal);

        Input(page, "Search").Input("le");
        ClickIn(page, "[data-lookup-values]", "Show");

        page.WaitForAssertion(() => Assert.Contains(_api.Queries, q => q.Contains("search=le", StringComparison.Ordinal) && q.Contains("sort=count", StringComparison.Ordinal)));
    }

    [Fact]
    public void ManualLookupsAreListedWithTheirScopeAndOnlyTheOwnedOnesCanBeChanged()
    {
        var page = RenderPage<LookupsPage>();

        var own = page.Find("[data-manual=nationality]");
        Assert.Contains("2 values", own.TextContent, StringComparison.Ordinal);
        Assert.Contains("this tenant", own.TextContent, StringComparison.Ordinal);
        Assert.Contains(own.QuerySelectorAll("button"), b => b.TextContent.Contains("Edit", StringComparison.Ordinal));

        var shared = page.Find("[data-manual=audience]");
        Assert.Contains("Shared with you", shared.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(shared.QuerySelectorAll("button"), b => b.TextContent.Contains("Edit", StringComparison.Ordinal) || b.TextContent.Contains("Delete", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewManualLookupSendsTheTypedValuesWithLabelsAndSynonyms()
    {
        var page = RenderPage<LookupsPage>();

        Click(page, "New manual lookup");
        Input(page, "Name").Input("audience");
        Input(page, "Description").Change("Who a document is for.");
        page.Find("[data-form=manual-lookup] textarea[aria-label=Values]").Input("managers | Managers | lead; leaders\nemployees\n\ncontractors | External staff");
        Click(page, "Create");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, "/api/knowledge-bases/manual-lookups")));
        var body = Body(HttpMethod.Post, "/api/knowledge-bases/manual-lookups");
        Assert.Equal(("audience", "Tenant"), (body.GetProperty("name").GetString(), body.GetProperty("scope").GetString()));
        var values = body.GetProperty("values").EnumerateArray().ToList();
        Assert.Equal(["managers", "employees", "contractors"], values.Select(v => v.GetProperty("value").GetString()));
        Assert.Equal("Managers", values[0].GetProperty("label").GetString());
        Assert.Equal(["lead", "leaders"], values[0].GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-form=manual-lookup]")));
    }

    [Fact]
    public void AManualLookupWithAnInvalidNameOrADuplicateValueIsNotSent()
    {
        var page = RenderPage<LookupsPage>();

        Click(page, "New manual lookup");
        Input(page, "Name").Input("Not Valid");
        page.Find("[data-form=manual-lookup] textarea[aria-label=Values]").Input("a\nA");
        Click(page, "Create");

        page.WaitForAssertion(() => Assert.Contains("lower-case letters", page.Find("[data-form=manual-lookup]").TextContent, StringComparison.Ordinal));
        Assert.Contains("listed twice", page.Find("[data-form=manual-lookup]").TextContent, StringComparison.Ordinal);
        Assert.Equal(0, _api.Count(HttpMethod.Post, "/api/knowledge-bases/manual-lookups"));
    }

    [Fact]
    public void ANameAlreadyTakenIsShownAndTheFormStaysOpen()
    {
        _manualSaveStatus = HttpStatusCode.Conflict;
        var page = RenderPage<LookupsPage>();

        Click(page, "New manual lookup");
        Input(page, "Name").Input("nationality");
        page.Find("[data-form=manual-lookup] textarea[aria-label=Values]").Input("IT");
        Click(page, "Create");

        page.WaitForAssertion(() => Assert.Contains("already exists", page.Markup, StringComparison.Ordinal));
        Assert.NotEmpty(page.FindAll("[data-form=manual-lookup]"));
    }

    [Fact]
    public void EditingAManualLookupLoadsItsValuesAndSavesWithAPut()
    {
        var page = RenderPage<LookupsPage>();

        ClickIn(page, "[data-manual=nationality]", "Edit");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=manual-lookup]")));
        var values = page.Find("[data-form=manual-lookup] textarea[aria-label=Values]");
        Assert.Equal("IT | Italian | Italy; Italia\nFR | French", values.TextContent.Replace("\r\n", "\n", StringComparison.Ordinal));

        values.Input("IT | Italian | Italy; Italia\nFR | French\nDE");
        Click(page, "Save changes");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/knowledge-bases/manual-lookups/{ManualId}")));
        var body = Body(HttpMethod.Put, $"/api/knowledge-bases/manual-lookups/{ManualId}");
        Assert.Equal(["IT", "FR", "DE"], body.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("value").GetString()));
    }

    [Fact]
    public void DeletingAManualLookupAsksFirst()
    {
        var page = RenderPage<LookupsPage>();

        ClickIn(page, "[data-manual=nationality]", "Delete");
        Assert.Equal(0, _api.Count(HttpMethod.Delete, $"/api/knowledge-bases/manual-lookups/{ManualId}"));
        ClickIn(page, "[data-manual=nationality]", "Delete lookup");

        page.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Delete, $"/api/knowledge-bases/manual-lookups/{ManualId}")));
    }

    private JsonElement Body(HttpMethod method, string path) =>
        JsonDocument.Parse(_api.LastBody(method, path)!).RootElement.Clone();

    private IRenderedComponent<TPage> RenderPage<TPage>()
        where TPage : IComponent
    {
        var page = Render<TPage>();
        page.WaitForAssertion(() => Assert.DoesNotContain("mud-progress-linear", page.Markup, StringComparison.Ordinal));
        return page;
    }

    private static void Click<TPage>(IRenderedComponent<TPage> page, string text)
        where TPage : IComponent =>
        page.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    private static void ClickIn<TPage>(IRenderedComponent<TPage> page, string selector, string text)
        where TPage : IComponent =>
        page.Find(selector).QuerySelectorAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    private static IElement Input<TPage>(IRenderedComponent<TPage> page, string label)
        where TPage : IComponent
    {
        var element = page.FindAll("label").First(l => l.TextContent.Replace("*", string.Empty, StringComparison.Ordinal).Trim() == label);
        return page.Find($"#{element.GetAttribute("for")}");
    }
}
