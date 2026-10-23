using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.KnowledgeBases.Search;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Tools.Rag;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.SampleKnowledgeBase;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Rag;

/// <summary>The model searches inside the documents it listed: <c>document_ids</c> on a rag tool (spec 007 D-14, AC-36 to AC-39).</summary>
public sealed partial class RagToolDocumentIdsTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static readonly string[] Hr = ["hr"];
    private static readonly string[] Travel = ["travel"];
    private static readonly string[] LeaveAndTravel = ["leave", "travel"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^\[(?<n>\d+)\] (?<header>.+)$", RegexOptions.Multiline)]
    private static partial Regex SourceLine();

    private static List<string> Titles(string result) =>
        [.. SourceLine().Matches(result).Select(m => m.Groups["header"].Value.Split(" · ")[1].Split(',')[0])];

    private static string ResultOf(IEnumerable<SseEvent> events) => events.Single(e => e.Type == "tool_result").Data.GetProperty("content").GetString()!;

    private static string StatusOf(IEnumerable<SseEvent> events) => events.Single(e => e.Type == "tool_result").Data.GetProperty("status").GetString()!;

    private static async Task<IReadOnlyList<SseEvent>> AskAsync(RagApi.Setup setup, object arguments)
    {
        setup.App.ModelServer.EnqueueRounds(ChatScript.CallTools(new FakeToolCall("rag__company", JsonSerializer.Serialize(arguments), "call_1")), ChatScript.Reply("Done."));
        return await ChatApi.SendAsync(setup.Admin, await ChatApi.CreateConversationAsync(setup.Admin), "Question");
    }

    private async Task<(RagApi.Setup Setup, Guid Tool)> StartAsync(object? fixedFilters = null, bool? allowDocumentIds = null)
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();
        var created = await RagApi.CreateOkAsync(
            setup.Admin,
            RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: fields.Values, fixedFilters: fixedFilters, topK: 20, allowDocumentIds: allowDocumentIds));
        await RagApi.ActivateAsync(setup.Admin, "rag__company");
        return (setup, created.Id);
    }

    [Fact] // AC-36
    public async Task ANewToolAllowsDocumentIdsAndItsDefinitionOffersThem()
    {
        var (setup, tool) = await StartAsync();
        await using var owner = setup;

        var detail = await RagApi.GetAsync(setup.Admin, tool);

        Assert.True(detail.AllowDocumentIds);
        var parameter = detail.Definition!.Parameter("document_ids");
        Assert.Equal("array", parameter.GetProperty("type").GetString());
        Assert.Equal("string", parameter.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(50, parameter.GetProperty("maxItems").GetInt32());
        Assert.DoesNotContain("document_ids", detail.Definition.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact] // AC-36
    public async Task AToolWithTheSwitchOffKeepsItsDefinitionWithoutDocumentIds()
    {
        var (setup, tool) = await StartAsync(allowDocumentIds: false);
        await using var owner = setup;

        var detail = await RagApi.GetAsync(setup.Admin, tool);

        Assert.False(detail.AllowDocumentIds);
        Assert.DoesNotContain("document_ids", detail.Definition!.Parameters);
    }

    [Fact] // AC-36
    public void TheSwitchIsPartOfTheHashOnlyWhenOn()
    {
        var kb = Guid.NewGuid();
        RagConfiguration Configuration(bool allow) =>
            new([kb], [], "{}", [], 5, null, 6000, ToolScopeKind.Tenant, [], [], AllowDocumentIds: allow);
        var withoutTheSwitch = new RagConfiguration([kb], [], "{}", [], 5, null, 6000, ToolScopeKind.Tenant, [], []);

        var off = RagToolHash.Compute("company", "Searches.", Configuration(allow: false));
        var on = RagToolHash.Compute("company", "Searches.", Configuration(allow: true));

        Assert.Equal(RagToolHash.Compute("company", "Searches.", withoutTheSwitch), off);
        Assert.NotEqual(off, on);
    }

    [Fact]
    public void TheParameterIsOfferedOnlyWhenAskedFor()
    {
        KnowledgeBaseInfo[] bases = [new(Guid.NewGuid(), "alpha", "Alpha.", [], MapsDocumentId: true)];

        var without = RagToolDefinition.Build("Searches.", bases, []);
        var with = RagToolDefinition.Build("Searches.", bases, [], offerDocumentIds: true);

        using var plain = JsonDocument.Parse(without.InputSchema);
        using var offered = JsonDocument.Parse(with.InputSchema);
        Assert.Equal(["query"], plain.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["query", "document_ids"], offered.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact] // AC-37
    public async Task OnlyChunksOfTheListedDocumentsAreSearched()
    {
        var (setup, _) = await StartAsync();
        await using var owner = setup;

        var events = await AskAsync(setup, new { query = "policy", document_ids = LeaveAndTravel });

        var titles = Titles(ResultOf(events));
        Assert.Equal("succeeded", StatusOf(events));
        Assert.Equal(["Annual leave policy", "Annual leave policy", "Travel expense rules", "Travel expense rules"], titles.Order(StringComparer.Ordinal));
    }

    [Fact] // AC-37
    public async Task MoreThanFiftyIdsIsAnErrorResultTheModelCanCorrect()
    {
        var (setup, _) = await StartAsync();
        await using var owner = setup;

        var events = await AskAsync(setup, new { query = "policy", document_ids = Enumerable.Range(0, 51).Select(n => $"doc-{n.ToString(CultureInfo.InvariantCulture)}") });

        Assert.Equal("failed", StatusOf(events));
        Assert.Contains("up to 50", ResultOf(events), StringComparison.Ordinal);
    }

    [Fact] // AC-37
    public async Task IdsThatAreNotAListOfTextsAreAnErrorResult()
    {
        var (setup, _) = await StartAsync();
        await using var owner = setup;

        var events = await AskAsync(setup, new { query = "policy", document_ids = "travel" });

        Assert.Equal("failed", StatusOf(events));
        Assert.Contains("document_ids", ResultOf(events), StringComparison.Ordinal);
    }

    [Fact] // AC-37
    public async Task AnEmptyListIsNoRestriction()
    {
        var (setup, _) = await StartAsync();
        await using var owner = setup;

        var events = await AskAsync(setup, new { query = "policy", document_ids = Array.Empty<string>() });

        Assert.True(Titles(ResultOf(events)).Distinct().Count() > 2);
    }

    [Fact] // AC-38
    public async Task TheListNeverWidensAFixedFilter()
    {
        var (setup, _) = await StartAsync(fixedFilters: new { department = Hr });
        await using var owner = setup;

        var events = await AskAsync(setup, new { query = "policy", document_ids = Travel });

        var result = ResultOf(events);
        Assert.Empty(Titles(result));
        Assert.Contains("Nothing was found", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Travel", result, StringComparison.Ordinal);
    }

    [Fact] // AC-39
    public async Task AKnowledgeBaseWithoutADocumentIdIsSkippedAndNamed()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var complete = await setup.RegisterAsync("complete");
        var bare = await setup.RegisterAsync("bare", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [bare, complete], topK: 20));
        await RagApi.ActivateAsync(setup.Admin, "rag__company");

        var events = await AskAsync(setup, new { query = "policy", document_ids = Travel });

        var result = ResultOf(events);
        Assert.Equal(["Travel expense rules", "Travel expense rules"], Titles(result));
        Assert.Contains("Not searched, because they have no field for the filters or the document ids used: bare.", result, StringComparison.Ordinal);
    }

    [Fact] // AC-39
    public async Task SavingWithTheSwitchOnWhenNoKnowledgeBaseMapsTheDocumentIdIsRefused()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var bare = await setup.RegisterAsync("bare", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [bare], allowDocumentIds: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.document_ids_not_supported", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-39
    public async Task LeavingTheSwitchOutWhenNoKnowledgeBaseMapsTheDocumentIdSavesTheToolWithItOff()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var bare = await setup.RegisterAsync("bare", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [bare]));

        Assert.False((await RagApi.GetAsync(setup.Admin, created.Id)).AllowDocumentIds);
        Assert.DoesNotContain("document_ids", created.Definition!.Parameters);
    }

    [Fact]
    public async Task ChangingAToolWithoutSayingKeepsItsSwitchAndChangesItsHash()
    {
        var (setup, tool) = await StartAsync(allowDocumentIds: false);
        await using var owner = setup;
        var before = await RagApi.GetAsync(setup.Admin, tool);

        using var kept = await setup.Admin.PutAsJsonAsync(RagApi.One(tool), RagApi.Body(knowledgeBaseIds: before.KnowledgeBaseIds, exposedFieldIds: before.ExposedFieldIds, topK: 20), Ct);
        Assert.Equal(HttpStatusCode.NoContent, kept.StatusCode);
        Assert.False((await RagApi.GetAsync(setup.Admin, tool)).AllowDocumentIds);

        using var turnedOn = await setup.Admin.PutAsJsonAsync(RagApi.One(tool), RagApi.Body(knowledgeBaseIds: before.KnowledgeBaseIds, exposedFieldIds: before.ExposedFieldIds, topK: 20, allowDocumentIds: true), Ct);
        Assert.Equal(HttpStatusCode.NoContent, turnedOn.StatusCode);
        Assert.True((await RagApi.GetAsync(setup.Admin, tool)).AllowDocumentIds);
    }

    [Fact]
    public async Task ASwitchTurnedOffRefusesTheParameterLikeAnyUnknownFilter()
    {
        var (setup, _) = await StartAsync(allowDocumentIds: false);
        await using var owner = setup;

        var events = await AskAsync(setup, new { query = "policy", document_ids = Travel });

        Assert.Equal("failed", StatusOf(events));
    }

    [Fact]
    public async Task ADocumentIdWithoutAPayloadIndexWarnsAndAnIndexedOneDoesNot()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var flat = await setup.RegisterAsync("flat");
        var nested = await setup.RegisterAsync("nested", SampleCollections.CreateNested, SearchApi.NestedMapping);

        var indexed = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(name: "indexed", knowledgeBaseIds: [flat]));
        var unindexed = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(name: "unindexed", knowledgeBaseIds: [nested]));

        Assert.DoesNotContain((await RagApi.GetAsync(setup.Admin, indexed.Id)).Warnings, w => w.Code == "rag.document_id_unindexed");
        var warning = Assert.Single((await RagApi.GetAsync(setup.Admin, unindexed.Id)).Warnings, w => w.Code == "rag.document_id_unindexed");
        Assert.Contains("nested", warning.Message, StringComparison.Ordinal);
        Assert.Contains("doc_id", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoWarningWhenTheSwitchIsOff()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nested = await setup.RegisterAsync("nested", SampleCollections.CreateNested, SearchApi.NestedMapping);
        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [nested], allowDocumentIds: false));

        Assert.DoesNotContain((await RagApi.GetAsync(setup.Admin, created.Id)).Warnings, w => w.Code == "rag.document_id_unindexed");
    }
}
