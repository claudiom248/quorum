using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client.Api;
using Quorum.Web.Client.Components;

namespace Quorum.Integration.Tests.Web;

/// <summary>The lookup tool editor of the Tools page: lookups with aliases, knowledge bases, fields, fixed filters, warnings and the definition preview (spec 007, AC-43).</summary>
public sealed class LookupToolsSectionTests : BunitContext
{
    private static readonly Guid DepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6001");
    private static readonly Guid YearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6002");
    private static readonly Guid ContractsBase = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6011");
    private static readonly Guid ArchiveBase = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6012");
    private static readonly Guid SharedBase = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6013");
    private static readonly Guid SupplierLookup = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6021");
    private static readonly Guid ArchiveSupplierLookup = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6022");
    private static readonly Guid ManualLookup = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6023");
    private static readonly Guid ToolId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6031");
    private static readonly Guid SharedTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6032");
    private static readonly Guid CreatedTool = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b6033");

    private static readonly Guid[] ContractsOnly = [ContractsBase];
    private static readonly Guid[] DepartmentOnly = [DepartmentId];
    private static readonly string[] Hr = ["hr"];

    private readonly FakeApiHandler _api = new();
    private HttpStatusCode _createStatus = HttpStatusCode.Created;

    public LookupToolsSectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new QuorumApi(_api.CreateClient()));

        _api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Get && path == "/api/tools/lookup")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new
                    {
                        id = ToolId,
                        name = "contracts",
                        lookups = new[] { new { id = SupplierLookup, name = "supplier" } },
                        knowledgeBases = new[] { new { id = ContractsBase, name = "contracts" } },
                        scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
                        state = "Enabled",
                        policy = "Auto",
                        shared = false,
                        warnings = new[] { new { code = "lookup_tool.lookup_missing", message = "1 of the lookups of this tool no longer exists." } },
                    },
                    new
                    {
                        id = SharedTool,
                        name = "partner",
                        lookups = Array.Empty<object>(),
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
                    new { id = ContractsBase, name = "contracts", description = "Contracts.", status = "Available", shared = false, kind = "Qdrant", scope = "Tenant" },
                    new { id = ArchiveBase, name = "archive", description = "Old contracts.", status = "Available", shared = false, kind = "Qdrant", scope = "Tenant" },
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

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{ContractsBase}/lookups")
            {
                return FakeApiHandler.Json(new[] { ComputedLookup(SupplierLookup, "supplier") });
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{ArchiveBase}/lookups")
            {
                return FakeApiHandler.Json(new[] { ComputedLookup(ArchiveSupplierLookup, "supplier") });
            }

            if (method == HttpMethod.Get && path == "/api/knowledge-bases/manual-lookups")
            {
                return FakeApiHandler.Json(new object[]
                {
                    new { id = ManualLookup, name = "nationality", description = "Nationalities.", scope = "Tenant", valueCount = 5, shared = false, scopeTenantIds = Array.Empty<Guid>() },
                });
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{ContractsBase}")
            {
                return FakeApiHandler.Json(KnowledgeBase(ContractsBase, "contracts", DepartmentId, "department"));
            }

            if (method == HttpMethod.Get && path == $"/api/knowledge-bases/{ArchiveBase}")
            {
                return FakeApiHandler.Json(KnowledgeBase(ArchiveBase, "archive", YearId, "year"));
            }

            if (path == "/api/assistants")
            {
                return FakeApiHandler.Json(new[] { new { id = Guid.NewGuid(), name = "Assistant" } });
            }

            if (method == HttpMethod.Get && path == $"/api/tools/lookup/{ToolId}")
            {
                return FakeApiHandler.Json(Detail(ToolId, "contracts", [new { lookupId = SupplierLookup, alias = (string?)"vendor" }], ContractsOnly, DepartmentOnly, new { department = Hr }));
            }

            if (method == HttpMethod.Get && path == $"/api/tools/lookup/{CreatedTool}")
            {
                return FakeApiHandler.Json(Detail(CreatedTool, "support", [new { lookupId = SupplierLookup, alias = (string?)null }], [], [], new { }));
            }

            if (method == HttpMethod.Post && path == "/api/tools/lookup")
            {
                return _createStatus == HttpStatusCode.Created
                    ? FakeApiHandler.Json(new { id = CreatedTool, definition = Definition("support"), warnings = Array.Empty<object>() }, HttpStatusCode.Created)
                    : FakeApiHandler.Json(
                        new { title = "Bad request", detail = "Two lookups would be called 'supplier'. Give one of them an alias.", code = "lookup_tool.alias_conflict" },
                        _createStatus);
            }

            if (method == HttpMethod.Put && path.StartsWith("/api/tools/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Delete && path == $"/api/tools/lookup/{ToolId}")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private static object ComputedLookup(Guid id, string name) => new
    {
        id,
        name,
        kind = "Computed",
        field = name,
        unit = "documents",
        slices = Array.Empty<object>(),
    };

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
            roles = new { text = "text", documentId = "document_id", title = (string?)null, position = (string?)null, sourceUrl = (string?)null },
            fields = new[] { new { path = fieldName, dictionaryFieldId = fieldId, name = fieldName } },
        },
        scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
    };

    private static object Definition(string name) => new
    {
        name = $"lookup__{name}",
        description = "Reads the lists of the company.\n\nLookups:\n- supplier: Suppliers.",
        inputSchema = new
        {
            type = "object",
            properties = new { operation = new { type = "string" }, lookup = new { type = "string" } },
            required = new[] { "lookup" },
            additionalProperties = false,
        },
        estimatedTokens = 640,
    };

    private static object Detail(Guid id, string name, object[] lookups, Guid[] bases, Guid[] exposed, object fixedFilters) => new
    {
        id,
        name,
        description = "Reads the lists of the company.",
        lookups,
        knowledgeBaseIds = bases,
        exposedFields = exposed.Select(field => new { fieldId = field, alias = (string?)null }).ToArray(),
        fixedFilters,
        scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
        state = "Enabled",
        policy = "Auto",
        definition = Definition(name),
        warnings = new[] { new { code = "lookup_tool.filter_unindexed", message = "The field 'department' has no payload index in contracts (department)." } },
    };

    private static JsonElement Body(FakeApiHandler api, HttpMethod method, string path) =>
        JsonDocument.Parse(api.LastBody(method, path)!).RootElement.Clone();

    private IRenderedComponent<LookupToolsSection> RenderSection()
    {
        var section = Render<LookupToolsSection>();
        section.WaitForAssertion(() => Assert.DoesNotContain("mud-progress-linear", section.Markup, StringComparison.Ordinal));
        return section;
    }

    private static void Click(IRenderedComponent<LookupToolsSection> section, string text) =>
        section.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    private static IElement Input(IRenderedComponent<LookupToolsSection> section, string label)
    {
        var element = section.FindAll("label").First(l => l.TextContent.Replace("*", string.Empty, StringComparison.Ordinal).Trim() == label);
        return section.Find($"#{element.GetAttribute("for")}");
    }

    private static void Tick(IRenderedComponent<LookupToolsSection> section, string label, bool on) =>
        section.FindAll("label").First(l => l.TextContent.Trim() == label).QuerySelector("input[type=checkbox]")!.Change(on);

    [Fact]
    public void TheToolsAreListedWithTheirListsTheirScopeAndTheirWarnings()
    {
        var section = RenderSection();

        var tool = section.Find("[data-lookup-tool=contracts]");
        Assert.Contains("lookup__contracts", tool.TextContent, StringComparison.Ordinal);
        Assert.Contains("supplier", tool.TextContent, StringComparison.Ordinal);
        Assert.Contains("also asks live: contracts", tool.TextContent, StringComparison.Ordinal);
        Assert.Contains("no longer exists", tool.QuerySelector("[data-warning=lookup_tool\\.lookup_missing]")!.TextContent, StringComparison.Ordinal);

        var shared = section.Find("[data-lookup-tool=partner]");
        Assert.Contains("Shared with you", shared.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(shared.QuerySelectorAll("button"), b => b.TextContent.Contains("Edit", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryLookupIsOfferedWithWhereItComesFrom()
    {
        var section = RenderSection();
        Click(section, "Add lookup tool");

        var labels = section.FindAll("[data-lookup-choice]").Select(c => c.GetAttribute("data-lookup-choice")).ToArray();

        Assert.Equal(["supplier · archive", "supplier · contracts", "nationality (typed by hand)"], labels);
    }

    [Fact] // AC-43
    public void SavingSendsTheLookupsWithTheirAliasesTheKnowledgeBasesTheFieldsAndTheFixedFilters()
    {
        var section = RenderSection();
        Click(section, "Add lookup tool");
        Input(section, "Name").Change("support");
        Input(section, "Description").Change("Reads the lists of the company.");
        Tick(section, "supplier · contracts", true);
        Tick(section, "nationality (typed by hand)", true);
        Input(section, "Alias of supplier · contracts").Input("vendor");
        Tick(section, "contracts", true);
        section.Find("select[aria-label='Add a fixed filter']").Change(DepartmentId.ToString());
        Input(section, "department is any of").Change("hr, legal");
        section.Find("[data-form=lookup] select[aria-label=Policy]").Change("Confirm");

        section.Find("[data-form=lookup] form").Submit();

        section.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, "/api/tools/lookup")));
        var body = Body(_api, HttpMethod.Post, "/api/tools/lookup");
        Assert.Equal(("support", "Confirm"), (body.GetProperty("name").GetString(), body.GetProperty("policy").GetString()));
        var lookups = body.GetProperty("lookups").EnumerateArray().ToList();
        Assert.Equal([SupplierLookup, ManualLookup], lookups.Select(l => l.GetProperty("lookupId").GetGuid()));
        Assert.Equal("vendor", lookups[0].GetProperty("alias").GetString());
        Assert.Equal(JsonValueKind.Null, lookups[1].GetProperty("alias").ValueKind);
        Assert.Equal([ContractsBase], body.GetProperty("knowledgeBaseIds").EnumerateArray().Select(v => v.GetGuid()));
        Assert.Equal([DepartmentId], body.GetProperty("exposedFields").EnumerateArray().Select(v => v.GetProperty("fieldId").GetGuid()));
        Assert.Equal(["hr", "legal"], body.GetProperty("fixedFilters").GetProperty("department").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("Tenant", body.GetProperty("scope").GetProperty("kind").GetString());
    }

    [Fact] // AC-43
    public void AfterSavingTheEditorShowsTheDefinitionItsSizeAndTheWarnings()
    {
        var section = RenderSection();
        Click(section, "Add lookup tool");
        Input(section, "Name").Change("support");
        Input(section, "Description").Change("Reads the lists of the company.");
        Tick(section, "supplier · contracts", true);

        section.Find("[data-form=lookup] form").Submit();

        section.WaitForAssertion(() => Assert.NotEmpty(section.FindAll("[data-definition]")));
        var definition = section.Find("[data-definition]");
        Assert.Contains("lookup__support", definition.TextContent, StringComparison.Ordinal);
        Assert.Contains("about 640 tokens of the context", definition.TextContent, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\": false", definition.TextContent, StringComparison.Ordinal);
        Assert.Contains("lookup__support was saved", section.Markup, StringComparison.Ordinal);
        Assert.Contains("no payload index", section.Find("[data-form=lookup] [data-warning=lookup_tool\\.filter_unindexed]").TextContent, StringComparison.Ordinal);
    }

    [Fact] // AC-24
    public void TwoListsWithTheSameNameAreFlaggedUntilOneGetsAnAlias()
    {
        var section = RenderSection();
        Click(section, "Add lookup tool");
        Input(section, "Name").Change("support");
        Input(section, "Description").Change("Reads the lists of the company.");
        Tick(section, "supplier · contracts", true);
        Tick(section, "supplier · archive", true);

        section.Find("[data-form=lookup] form").Submit();

        section.WaitForAssertion(() => Assert.Contains("Two lookups would be called \"supplier\"", section.Markup, StringComparison.Ordinal));
        Assert.Equal(0, _api.Count(HttpMethod.Post, "/api/tools/lookup"));

        Input(section, "Alias of supplier · archive").Input("old_supplier");
        section.Find("[data-form=lookup] form").Submit();

        section.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Post, "/api/tools/lookup")));
    }

    [Fact]
    public void ALookupToolNeedsALookupAndTheServerAnswerIsShownWhenItRefuses()
    {
        var section = RenderSection();
        Click(section, "Add lookup tool");
        Input(section, "Name").Change("support");
        Input(section, "Description").Change("Reads the lists of the company.");

        section.Find("[data-form=lookup] form").Submit();

        section.WaitForAssertion(() => Assert.Contains("Choose at least one lookup", section.Markup, StringComparison.Ordinal));
        Assert.Equal(0, _api.Count(HttpMethod.Post, "/api/tools/lookup"));

        _createStatus = HttpStatusCode.BadRequest;
        Tick(section, "supplier · contracts", true);
        section.Find("[data-form=lookup] form").Submit();

        section.WaitForAssertion(() => Assert.Contains("Give one of them an alias", section.Markup, StringComparison.Ordinal));
        Assert.NotEmpty(section.FindAll("[data-form=lookup]"));
    }

    [Fact]
    public void TheOperationsFollowWhetherTheToolAsksKnowledgeBasesLive()
    {
        var section = RenderSection();
        Click(section, "Add lookup tool");
        Tick(section, "supplier · contracts", true);

        Assert.Equal("values", section.Find("[data-operations] .tool-name").TextContent);

        Tick(section, "contracts", true);

        Assert.Equal("values, documents, count", section.Find("[data-operations] .tool-name").TextContent);
    }

    [Fact]
    public void TheFieldsOfTheKnowledgeBasesOfTheChosenListsAreOffered()
    {
        var section = RenderSection();
        Click(section, "Add lookup tool");

        Tick(section, "supplier · contracts", true);

        section.WaitForAssertion(() => Assert.Contains("department (text)", section.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("year (number)", section.Markup, StringComparison.Ordinal);

        Tick(section, "supplier · archive", true);

        section.WaitForAssertion(() => Assert.Contains("year (number)", section.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void EditingLoadsTheBindingAndSavesWithAPut()
    {
        var section = RenderSection();

        Click(section.Find("[data-lookup-tool=contracts]"), "Edit");
        section.WaitForAssertion(() => Assert.NotEmpty(section.FindAll("[data-form=lookup]")));

        Assert.Equal("contracts", Input(section, "Name").GetAttribute("value"));
        Assert.Equal("vendor", Input(section, "Alias of supplier · contracts").GetAttribute("value"));
        Assert.Equal("hr", Input(section, "department is any of").GetAttribute("value"));
        Assert.NotEmpty(section.FindAll("[data-definition]"));

        section.Find("[data-form=lookup] form").Submit();

        section.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Put, $"/api/tools/lookup/{ToolId}")));
        var body = Body(_api, HttpMethod.Put, $"/api/tools/lookup/{ToolId}");
        Assert.Equal("vendor", body.GetProperty("lookups")[0].GetProperty("alias").GetString());
        Assert.Equal(["hr"], body.GetProperty("fixedFilters").GetProperty("department").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void DeletingAToolAsksFirst()
    {
        var section = RenderSection();

        Click(section.Find("[data-lookup-tool=contracts]"), "Delete");
        Assert.Equal(0, _api.Count(HttpMethod.Delete, $"/api/tools/lookup/{ToolId}"));
        Click(section.Find("[data-lookup-tool=contracts]"), "Delete tool");

        section.WaitForAssertion(() => Assert.Equal(1, _api.Count(HttpMethod.Delete, $"/api/tools/lookup/{ToolId}")));
    }

    [Fact]
    public void WithoutAnyLookupTheEditorPointsToTheLookupsPage()
    {
        var script = _api.Respond;
        _api.Respond = (method, path) =>
            method == HttpMethod.Get && path == "/api/knowledge-bases/manual-lookups"
                ? FakeApiHandler.Json(Array.Empty<object>())
                : method == HttpMethod.Get && path.EndsWith("/lookups", StringComparison.Ordinal)
                    ? FakeApiHandler.Json(Array.Empty<object>())
                    : script(method, path);
        var section = RenderSection();

        Click(section, "Add lookup tool");

        Assert.Contains("No lookup exists yet", section.Markup, StringComparison.Ordinal);
        Assert.Equal("knowledge/lookups", section.Find("[data-form=lookup] a[href]").GetAttribute("href"));
    }

    private static void Click(IElement scope, string text) =>
        scope.QuerySelectorAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();
}
