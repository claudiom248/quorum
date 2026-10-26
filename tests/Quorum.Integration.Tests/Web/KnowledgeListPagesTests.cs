using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Web.Client.Pages;

namespace Quorum.Integration.Tests.Web;

/// <summary>The knowledge bases, lookups and dictionary lists with their drawers (spec 010 AC-3, AC-11, AC-16, AC-19, AC-20).</summary>
public sealed class KnowledgeListPagesTests : ListPageTestContext
{
    private static readonly Guid OwnId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6001");
    private static readonly Guid BrokenId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6002");
    private static readonly Guid SharedId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6003");
    private static readonly Guid ManualId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6011");
    private static readonly Guid DerivedId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6012");
    private static readonly Guid YearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6021");
    private static readonly Guid SharedFieldId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6022");

    private static readonly object[] MappedFields = [new { path = "department", dictionaryFieldId = YearId, name = "department" }];

    private static readonly string[] DetailActions = ["Verify", "Test search", "Edit", "Delete"];

    private static readonly string[] Italia = ["Italia"];

    private HttpStatusCode _deleteFieldStatus = HttpStatusCode.NoContent;

    public KnowledgeListPagesTests()
    {
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/knowledge-bases")
            {
                return Page(
                    new { id = OwnId, name = "policies", description = "Company policies.", status = "Available", shared = false, kind = "Qdrant", scope = NoScopeIds("Tenant"), lastVerifiedAt = (DateTimeOffset?)DateTimeOffset.UtcNow, warning = (string?)null },
                    new { id = BrokenId, name = "manuals", description = "Product manuals.", status = "Unavailable", shared = false, kind = "Qdrant", scope = NoScopeIds("Global"), lastVerifiedAt = (DateTimeOffset?)null, warning = "The collection manuals was not found." },
                    new { id = SharedId, name = "handbook", description = "Shared handbook.", status = "Available", shared = true, scope = NoScopeIds("Global") });
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
                    connection = new { url = "http://qdrant:6334", collection = "policies", vectorName = (string?)null, hasApiKey = false },
                    embeddingModel = "nomic-embed-text",
                    queryPrefix = (string?)null,
                    mapping = new
                    {
                        roles = new { text = "text", documentId = (string?)null, title = (string?)null, position = (string?)null, sourceUrl = (string?)null },
                        fields = MappedFields,
                    },
                    scope = NoScopeIds("Tenant"),
                    warning = (string?)null,
                    lastVerifiedAt = (DateTimeOffset?)DateTimeOffset.UtcNow,
                });
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{OwnId}/lookups")
            {
                return FakeApiHandler.Json(new[] { new { id = DerivedId, name = "department", kind = "Computed", field = "department", unit = "documents", slices = Array.Empty<object>() } });
            }

            if (method == HttpMethod.Post && path == $"/api/knowledge-bases/{OwnId}/verify")
            {
                return FakeApiHandler.Json(new { status = "Available", warnings = Array.Empty<object>() });
            }

            if (method == HttpMethod.Delete && path == $"/api/knowledge-bases/{OwnId}")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases/lookups")
            {
                return Page(
                    new { id = ManualId, name = "nationality", kind = "Manual", knowledgeBase = (object?)null, field = (string?)null, unit = (string?)null, scope = NoScopeIds("Tenant"), valueCount = 2, shared = false },
                    new { id = DerivedId, name = "department", kind = "Derived", knowledgeBase = new { id = OwnId, name = "policies" }, field = "department", unit = "Documents", scope = NoScopeIds("Tenant"), valueCount = 5, shared = false });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases/manual-lookups")
            {
                return Page(new { id = ManualId, name = "nationality", description = "Where people come from.", scope = NoScopeIds("Tenant"), valueCount = 2, shared = false });
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/lookups/{ManualId}/values")
            {
                return FakeApiHandler.Json(new
                {
                    unit = (string?)null,
                    asOf = (DateTimeOffset?)null,
                    stale = false,
                    truncated = false,
                    values = new[] { new { value = "IT", label = "Italy", synonyms = Italia, count = (int?)null, attributes = (object?)null } },
                    cursor = (string?)null,
                    cursorRestarted = false,
                });
            }

            if (method == HttpMethod.Post && path == "/api/knowledge-bases/manual-lookups")
            {
                return FakeApiHandler.Json(new { id = Guid.NewGuid() }, HttpStatusCode.Created);
            }

            if (method == HttpMethod.Put && path == $"/api/knowledge-bases/manual-lookups/{ManualId}")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases/dictionary")
            {
                return Page(
                    new { id = YearId, name = "year", type = "Number", description = "The year.", scope = NoScopeIds("Tenant"), usedByCount = 1, cluster = (string?)"hr", shared = false },
                    new { id = SharedFieldId, name = "country", type = "Text", description = (string?)null, scope = NoScopeIds("Global"), usedByCount = 0, cluster = (string?)null, shared = true });
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
                return FakeApiHandler.Json(new[] { new { id = Guid.NewGuid(), name = "Assistant" } });
            }

            if (path == "/api/tenants")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    [Fact] // AC-3
    public void TheKnowledgeBasesAreListedWithKindStatusScopeAndTheirOwnersView()
    {
        var page = Visit<Quorum.Web.Client.Pages.KnowledgeBases>("/knowledge/bases");

        var own = page.Find("[data-kb=policies]").ParentElement!.ParentElement!;
        Assert.Contains("Qdrant", own.TextContent, StringComparison.Ordinal);
        Assert.Contains("Available", own.TextContent, StringComparison.Ordinal);
        var broken = page.Find("[data-kb=manuals]").ParentElement!.ParentElement!;
        Assert.Contains("Unavailable", broken.TextContent, StringComparison.Ordinal);
        Assert.Contains("Global", broken.TextContent, StringComparison.Ordinal);
        Assert.Contains("not yet", broken.TextContent, StringComparison.Ordinal);
        Assert.Contains("Shared with you", page.Find("[data-kb=handbook]").ParentElement!.ParentElement!.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-20
    public void RegisteringOpensTheWizardPageAndTheDetailOffersTheOwnersActions()
    {
        var page = Visit<Quorum.Web.Client.Pages.KnowledgeBases>("/knowledge/bases");
        page.Find("button[aria-label=\"Register knowledge base\"]").Click();
        Assert.EndsWith("/knowledge/bases/new", Services.GetService<Microsoft.AspNetCore.Components.NavigationManager>()!.Uri, StringComparison.Ordinal);
    }

    [Fact] // AC-20
    public void TheDetailShowsTheMappingTheLookupsAndTheOwnersActions()
    {
        var page = Visit<Quorum.Web.Client.Pages.KnowledgeBases>($"/knowledge/bases/{OwnId}", p => p.Add(c => c.Id, OwnId.ToString()));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-detail=knowledge-base]")));
        var detail = page.Find("[data-detail=knowledge-base]");
        Assert.Contains("1 mapped field(s): department", detail.TextContent, StringComparison.Ordinal);
        Assert.Contains("department", page.Find("[data-lookups]").TextContent, StringComparison.Ordinal);
        foreach (var action in DetailActions)
        {
            Assert.Contains(detail.QuerySelectorAll("button, a"), b => b.TextContent.Contains(action, StringComparison.Ordinal));
        }
    }

    [Fact] // AC-20
    public void VerifyingAsksTheServerAndSaysTheOutcome()
    {
        var page = Visit<Quorum.Web.Client.Pages.KnowledgeBases>($"/knowledge/bases/{OwnId}", p => p.Add(c => c.Id, OwnId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-detail=knowledge-base]")));

        ClickButton(page, "Verify");

        page.WaitForAssertion(() => Assert.Contains("Verified: available", page.Markup, StringComparison.Ordinal));
        Assert.Equal(1, Api.Count(HttpMethod.Post, $"/api/knowledge-bases/{OwnId}/verify"));
    }

    [Fact] // AC-16
    public void DeletingARegistrationAsksFirstAndThenRemovesIt()
    {
        var page = Visit<Quorum.Web.Client.Pages.KnowledgeBases>($"/knowledge/bases/{OwnId}", p => p.Add(c => c.Id, OwnId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-detail=knowledge-base]")));

        ClickButton(page, "Delete");
        Assert.Equal(0, Api.Count(HttpMethod.Delete, "/api/knowledge-bases"));
        Dialogs.WaitForAssertion(() => Assert.Contains("The data in the store is not touched", Dialogs.Markup, StringComparison.Ordinal));
        ClickButton(Dialogs, "Delete registration");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Delete, $"/api/knowledge-bases/{OwnId}")));
    }

    [Fact] // AC-20
    public void ASharedKnowledgeBaseShowsNoActions()
    {
        var script = Api.Respond;
        Api.Respond = (method, path) => method == HttpMethod.Get && path == $"/api/knowledge-bases/{SharedId}"
            ? FakeApiHandler.Json(new { id = SharedId, name = "handbook", description = "Shared handbook.", status = "Available", shared = true })
            : script(method, path);

        var page = Visit<Quorum.Web.Client.Pages.KnowledgeBases>($"/knowledge/bases/{SharedId}", p => p.Add(c => c.Id, SharedId.ToString()));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-detail=knowledge-base]")));
        Assert.DoesNotContain(page.Find("[data-detail=knowledge-base]").QuerySelectorAll("button"), b => b.TextContent.Contains("Delete", StringComparison.Ordinal));
    }

    [Fact] // AC-19
    public void ManualAndDerivedLookupsAreListedTogetherAndTheKindFilterIsSent()
    {
        var page = Visit<Lookups>("/knowledge/lookups?type=Derived");

        var manual = page.Find("[data-lookup=nationality]").ParentElement!.ParentElement!;
        Assert.Contains("Manual", manual.TextContent, StringComparison.Ordinal);
        Assert.Contains("typed by hand", manual.TextContent, StringComparison.Ordinal);
        var derived = page.Find("[data-lookup=department]").ParentElement!.ParentElement!;
        Assert.Contains("policies · department", derived.TextContent, StringComparison.Ordinal);
        Assert.Contains(Api.Queries, q => q.Contains("kind=Derived", StringComparison.Ordinal) && !q.Contains("type=", StringComparison.Ordinal));
    }

    [Fact] // AC-19
    public void AManualLookupOpensInTheFormWithItsValuesAndSavesWithAPut()
    {
        var page = Visit<Lookups>($"/knowledge/lookups/{ManualId}", p => p.Add(c => c.Id, ManualId.ToString()));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=manual-lookup]")));
        Assert.Contains("IT | Italy | Italia", page.Find("textarea[aria-label=Values]").TextContent, StringComparison.Ordinal);
        ClickButton(page, "Save changes");

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/knowledge-bases/manual-lookups/{ManualId}")));
        Assert.Equal("Where people come from.", Body(Api, HttpMethod.Put, $"/api/knowledge-bases/manual-lookups/{ManualId}").GetProperty("description").GetString());
    }

    [Fact] // AC-19
    public void ADerivedLookupIsShownReadOnlyWithAWayToItsKnowledgeBase()
    {
        var page = Visit<Lookups>($"/knowledge/lookups/{DerivedId}", p => p.Add(c => c.Id, DerivedId.ToString()));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-detail=lookup]")));
        Assert.Contains("Computed from", page.Find("[data-detail=lookup]").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-form=manual-lookup]"));
        Assert.Contains(page.FindAll("[data-detail=lookup] a"), a => a.GetAttribute("href") == $"/knowledge/bases/{OwnId}");
    }

    [Fact] // AC-3
    public void TheDictionaryIsListedAndASharedFieldIsMarked()
    {
        var page = Visit<Dictionary>("/knowledge/dictionary");

        var year = page.Find("[data-field=year]").ParentElement!.ParentElement!;
        Assert.Contains("hr", year.TextContent, StringComparison.Ordinal);
        Assert.Contains("Number", year.TextContent, StringComparison.Ordinal);
        Assert.Contains("Shared with you", page.Find("[data-field=country]").ParentElement!.ParentElement!.TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-3
    public void TheDictionaryOffersNoAssistantScopeOrAgentFilter()
    {
        var page = Visit<Dictionary>("/knowledge/dictionary");

        Assert.DoesNotContain(page.FindAll("label"), l => l.TextContent == "Agent");
    }

    [Fact]
    public void AddingAFieldSendsTheClusterAndScope()
    {
        var page = Visit<Dictionary>("/knowledge/dictionary/new", p => p.Add(c => c.Id, "new"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-form=field]")));
        var inputs = page.Find("[data-form=field]").QuerySelectorAll("input[type=text]");
        inputs[0].Change("region");
        inputs[1].Change("sales");

        page.Find("[data-form=field]").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Post, "/api/knowledge-bases/dictionary")));
        var body = Body(Api, HttpMethod.Post, "/api/knowledge-bases/dictionary");
        Assert.Equal(("region", "sales", "Tenant"), (body.GetProperty("name").GetString(), body.GetProperty("cluster").GetString(), body.GetProperty("scope").GetString()));
    }

    [Fact]
    public void EditingTheDescriptionSavesWithAPut()
    {
        var page = Visit<Dictionary>($"/knowledge/dictionary/{YearId}", p => p.Add(c => c.Id, YearId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-detail=field] form")));
        page.Find("[data-detail=field] textarea").Change("The year of the document.");

        page.Find("[data-detail=field] form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, Api.Count(HttpMethod.Put, $"/api/knowledge-bases/dictionary/{YearId}")));
        Assert.Equal("The year of the document.", Body(Api, HttpMethod.Put, $"/api/knowledge-bases/dictionary/{YearId}").GetProperty("description").GetString());

    }

    [Fact]
    public void ASharedFieldCannotBeChanged()
    {
        var shared = Visit<Dictionary>($"/knowledge/dictionary/{SharedFieldId}", p => p.Add(c => c.Id, SharedFieldId.ToString()));
        shared.WaitForAssertion(() => Assert.Contains("You can use it but not change it", shared.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void DeletingAFieldInUseShowsWhoUsesIt()
    {
        _deleteFieldStatus = HttpStatusCode.Conflict;
        var page = Visit<Dictionary>($"/knowledge/dictionary/{YearId}", p => p.Add(c => c.Id, YearId.ToString()));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-detail=field] form")));

        ClickButton(page, "Delete");
        Dialogs.WaitForAssertion(() => Assert.Contains("Models can no longer filter on it", Dialogs.Markup, StringComparison.Ordinal));
        ClickButton(Dialogs, "Delete field");

        page.WaitForAssertion(() => Assert.Contains("The field is used by knowledge bases policies", page.Markup, StringComparison.Ordinal));
    }

    [Fact] // AC-19
    public void TheKnowledgeBaseFilterIsOfferedAndTravelsToTheServer()
    {
        var page = Visit<Lookups>($"/knowledge/lookups?knowledgeBase={OwnId}");

        Assert.Contains(Api.Queries, q => q.Contains($"knowledgeBase={OwnId}", StringComparison.Ordinal));
        Assert.Contains(page.FindAll("label"), l => l.TextContent == "Knowledge base");
        Assert.Contains("Knowledge base: policies", page.Find("[data-filter=knowledgeBase]").TextContent, StringComparison.Ordinal);
    }
}
