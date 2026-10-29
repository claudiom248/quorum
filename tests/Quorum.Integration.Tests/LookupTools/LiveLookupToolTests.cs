using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Lookups;
using Quorum.Integration.Tests.Rag;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.SampleKnowledgeBase;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.LookupTools;

/// <summary>
/// The live operations of a lookup tool over the contracts of the sample collection: documents, count and values among the documents
/// that match filters the model chose (spec 007 D-7, AC-27, AC-29, AC-30, AC-31, AC-32). The contracts: c1 Acme/facilities/all (3 chunks),
/// c2 Acme/it/it (2), c3 Globex/it/it+managers (2), c4 Initech/legal/managers (1), c5 Acme/facilities/all (2), c6 Globex/finance/managers (1).
/// </summary>
public sealed class LiveLookupToolTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] ContractFields =
    [
        new { name = "supplier", type = "Text" },
        new { name = "department", type = "Text" },
        new { name = "nationality", type = "Text" },
        new { name = "audience", type = "TextList" },
    ];

    private static object Mapping(bool countedPoint, bool listable = true) => new
    {
        roles = new { text = "text", documentId = "document_id", title = "title", sourceUrl = "source_url" },
        fields = MappedPaths
            .Select(name => new { path = name, name, listable = listable && name is "supplier" or "audience" })
            .ToArray(),
        countedPoint = countedPoint ? new { path = "is_first_chunk", value = true } : null,
    };

    private static readonly string[] MappedPaths = ["supplier", "department", "nationality", "audience"];

    private static readonly string[] Managers = ["managers"];

    private static readonly string[] Manager = ["manager"];

    private static readonly string[] Nowhere = ["nowhere"];

    private static readonly string[] ItAudience = ["it"];

    private static readonly string[] American = ["US"];

    private static readonly string[] AcmeOnly = ["Acme"];

    private static readonly string[] ItDepartment = ["it"];

    private sealed record Live(RagApi.Setup Setup, Guid KnowledgeBase, Guid Tool);

    private async Task<Live> StartAsync(
        bool countedPoint = false,
        bool listable = true,
        Func<string, SampleDataset>? dataset = null,
        object? fixedFilters = null,
        bool withKnowledgeBase = true,
        FakeTimeProvider? clock = null)
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant, clock);
        // The stored lists are not what these tests read: the computation is held back and a list stays "being prepared".
        setup.App.LookupComputer.Handler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");
        };

        var knowledgeBase = await setup.RegisterAsync("contracts", dataset ?? SampleCollections.CreateLookups, Mapping(countedPoint, listable), ContractFields);
        var fields = await setup.FieldsAsync();
        var manual = await LookupApi.CreateManualOkAsync(setup.Admin);

        var lookups = new List<object> { LookupToolApi.Bind(manual) };
        if (listable)
        {
            await using var services = setup.App.Services.CreateAsyncScope();
            var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var supplier = await db.Lookups.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(l => l.KnowledgeBaseId == knowledgeBase && l.DictionaryFieldId == fields["supplier"], Ct);
            lookups.Add(LookupToolApi.Bind(supplier.Id, "supplier"));
            var audience = await db.Lookups.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(l => l.KnowledgeBaseId == knowledgeBase && l.DictionaryFieldId == fields["audience"], Ct);
            lookups.Add(LookupToolApi.Bind(audience.Id, "audience"));
        }

        var tool = await LookupToolApi.CreateOkAsync(
            setup.Admin,
            LookupToolApi.Body(
                lookups: [.. lookups],
                knowledgeBaseIds: withKnowledgeBase ? [knowledgeBase] : null,
                // A field a single value fixes is not offered to the model (spec 012 D-7): the fixed filters of these tests fix department.
                exposedFields: fixedFilters is null ? [new { fieldId = fields["audience"] }, new { fieldId = fields["department"] }] : [new { fieldId = fields["audience"] }],
                fixedFilters: fixedFilters));
        await ToolFixtures.ActivateOnlyAsync(setup.Admin, "lookup__lists");
        return new Live(setup, knowledgeBase, tool.Id);
    }

    // Each thing the tool does is a function of its own: an `operation` in the arguments says which one the call is for.
    private static FakeToolCall Call(object arguments, string id = "call_1")
    {
        var node = JsonSerializer.SerializeToNode(arguments)!.AsObject();
        var operation = node["operation"]?.GetValue<string>() ?? "values";
        node.Remove("operation");
        return new($"lookup_{operation}__lists", node.ToJsonString(), id);
    }

    private static Task<IReadOnlyList<SseEvent>> AskAsync(RagApi.Setup setup, params FakeToolCall[] calls)
    {
        setup.App.ModelServer.EnqueueRounds([.. calls.Select(c => ChatScript.CallTools(c)), ChatScript.Reply("Done.")]);
        return AskCoreAsync(setup);
    }

    private static async Task<IReadOnlyList<SseEvent>> AskCoreAsync(RagApi.Setup setup)
    {
        var id = await ChatApi.CreateConversationAsync(setup.Admin);
        return await ChatApi.SendAsync(setup.Admin, id, "Question");
    }

    private static string ResultOf(IEnumerable<SseEvent> events, int index = 0) =>
        events.Where(e => e.Type == "tool_result").ElementAt(index).Data.GetProperty("content").GetString()!;

    private static string StatusOf(IEnumerable<SseEvent> events, int index = 0) =>
        events.Where(e => e.Type == "tool_result").ElementAt(index).Data.GetProperty("status").GetString()!;

    private static List<string> Documents(string result)
    {
        var lines = result.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("<<<DOCUMENTS-", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, l => l.StartsWith("<<<END-DOCUMENTS-", StringComparison.Ordinal));
        return start < 0 || end < 0 ? [] : [.. lines[(start + 1)..end]];
    }

    private static string CursorOf(string result)
    {
        var from = result.IndexOf("cursor \"", StringComparison.Ordinal) + "cursor \"".Length;
        return result[from..result.IndexOf('"', from)];
    }

    [Fact] // AC-27
    public async Task TheDefinitionOffersDocumentsAndCountAndTheFiltersOfTheExposedFields()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var detail = await LookupToolApi.GetAsync(live.Setup.Admin, live.Tool);

        // One function for each thing the tool does, each with only its own arguments.
        Assert.Equal(["lookup_values__lists", "lookup_documents__lists", "lookup_count__lists"], detail.Definitions.Select(d => d.Name));
        Assert.Equal(["audience", "cursor", "department", "limit", "lookup", "search", "sort"], detail.Function("values").Parameters.Order().ToArray());
        Assert.Equal(["audience", "cursor", "department", "limit"], detail.Function("documents").Parameters.Order().ToArray());
        Assert.Equal(["audience", "department"], detail.Function("count").Parameters.Order().ToArray());
        Assert.Contains("contracts", detail.Function("documents").Description, StringComparison.Ordinal);
        Assert.False(detail.Function("documents").InputSchema.TryGetProperty("required", out var required), required.ToString());
        Assert.Equal(detail.Definition!.Name, detail.Function("values").Name);
    }

    [Fact] // AC-27
    public async Task WithNoKnowledgeBaseTheLiveOperationsAreNotOffered()
    {
        var live = await StartAsync(withKnowledgeBase: false);
        await using var _ = live.Setup;

        var detail = await LookupToolApi.GetAsync(live.Setup.Admin, live.Tool);

        Assert.Equal("lookup_values__lists", Assert.Single(detail.Definitions).Name);
        var events = await AskAsync(live.Setup, Call(new { operation = "count" }));
        Assert.Equal("failed", StatusOf(events));
        // A function that was not offered is not found: the model never had it.
        Assert.Contains("lookup_count__lists", ResultOf(events), StringComparison.Ordinal);
    }

    [Theory] // AC-29
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentsReturnsEachMatchingDocumentOnceWithItsIdTitleUrlAndExposedFields(bool countedPoint)
    {
        var live = await StartAsync(countedPoint);
        await using var _ = live.Setup;

        var events = await AskAsync(live.Setup, Call(new { operation = "documents", audience = Managers }));

        var result = ResultOf(events);
        Assert.Equal("succeeded", StatusOf(events));
        var rows = Documents(result);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Contains("Cloud hosting agreement", StringComparison.Ordinal) && r.Contains("id: c3", StringComparison.Ordinal)
            && r.Contains("/c3", StringComparison.Ordinal) && r.Contains("audience: it, managers", StringComparison.Ordinal) && r.Contains("department: it", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("id: c4", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("id: c6", StringComparison.Ordinal));
        Assert.StartsWith("Documents from the knowledge bases", result, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor", result, StringComparison.Ordinal);
        var audit = Assert.Single(await ToolFixtures.AuditRowsAsync(live.Setup.App));
        Assert.Equal(3, audit.SourceCount);
    }

    [Fact]
    public async Task AFilterValueThatMatchesNothingPointsToTheListThatHoldsTheExactValues()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var events = await AskAsync(live.Setup, Call(new { operation = "documents", audience = Manager }));

        var result = ResultOf(events);
        Assert.Equal("succeeded", StatusOf(events));
        Assert.Contains("No document matches.", result, StringComparison.Ordinal);
        Assert.Contains("the values of 'audience' are in the list 'audience'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACountOfZeroPointsToTheListTooButAFilterWithoutAListGetsNoHint()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var withList = ResultOf(await AskAsync(live.Setup, Call(new { operation = "count", audience = Manager })));
        Assert.Contains("the values of 'audience' are in the list 'audience'", withList, StringComparison.Ordinal);

        live.Setup.App.ModelServer.EnqueueRounds([ChatScript.CallTools(Call(new { operation = "documents", department = Nowhere })), ChatScript.Reply("Done.")]);
        var without = ResultOf(await AskCoreAsync(live.Setup));
        Assert.Contains("No document matches.", without, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing matched", without, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefinitionSaysWhereTheValuesOfAFilterAreAndWhatEachListIs()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var definition = (await LookupToolApi.GetAsync(live.Setup.Admin, live.Tool)).Definition!;

        Assert.Contains("the exact values are in the list 'audience'", definition.Parameter("audience").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("(from contracts; counts documents; only the values that occur in the documents; can be filtered by audience, department)", definition.Description, StringComparison.Ordinal);
        Assert.Contains("typed by hand", definition.Description, StringComparison.Ordinal);
    }

    [Theory] // AC-29
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentsPagesWithACursorAndNoDocumentComesTwice(bool countedPoint)
    {
        var live = await StartAsync(countedPoint);
        await using var _ = live.Setup;

        var first = ResultOf(await AskAsync(live.Setup, Call(new { operation = "documents", audience = Managers, limit = 2 })));
        var second = ResultOf(await AskAsync(live.Setup, Call(new { operation = "documents", audience = Managers, limit = 2, cursor = CursorOf(first) })));

        var all = Documents(first).Concat(Documents(second)).ToList();
        Assert.Equal(2, Documents(first).Count);
        Assert.Contains("cursor", first, StringComparison.Ordinal);
        Assert.Single(Documents(second));
        Assert.DoesNotContain("cursor", second, StringComparison.Ordinal);
        Assert.Equal(3, all.Count);
        foreach (var id in new[] { "c3", "c4", "c6" })
        {
            Assert.Single(all, r => r.Contains($"| id: {id} |", StringComparison.Ordinal));
        }
    }

    [Fact] // AC-29
    public async Task ACursorThatIsNotOneOfTheSearchIsAnErrorResult()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var events = await AskAsync(live.Setup, Call(new { operation = "documents", cursor = "not-a-cursor" }));

        Assert.Equal("failed", StatusOf(events));
        Assert.Contains("cursor", ResultOf(events), StringComparison.Ordinal);
    }

    [Fact] // AC-29, D-7
    public async Task TheFixedFiltersOfTheToolAlwaysApplyToDocuments()
    {
        var live = await StartAsync(fixedFilters: new { department = ItAudience });
        await using var _ = live.Setup;

        var events = await AskAsync(live.Setup, Call(new { operation = "documents", audience = Managers }));

        // c3 is the only contract of the it department that is for managers; the model's filter can only narrow the fixed one.
        var rows = Documents(ResultOf(events));
        Assert.Single(rows);
        Assert.Contains("id: c3", rows[0], StringComparison.Ordinal);
    }

    [Fact] // AC-30
    public async Task CountIsExactAndStatesTheUnitWithACountedPoint()
    {
        var live = await StartAsync(countedPoint: true);
        await using var _ = live.Setup;

        var result = ResultOf(await AskAsync(live.Setup, Call(new { operation = "count", audience = ItAudience })));

        Assert.Contains("2 documents in contracts", result, StringComparison.Ordinal);
        Assert.Contains("audience", result, StringComparison.Ordinal);
        Assert.Contains("plus the filters this tool always applies", result, StringComparison.Ordinal);
    }

    [Fact] // AC-30
    public async Task WithoutACountedPointTheCountIsOfChunksAndSaysSo()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var result = ResultOf(await AskAsync(live.Setup, Call(new { operation = "count", audience = ItAudience })));

        // c2 has two chunks and c3 two: four records, which are not four documents.
        Assert.Contains("4 chunks in contracts", result, StringComparison.Ordinal);
        Assert.Contains("do not present the number as a number of documents", result, StringComparison.Ordinal);
    }

    [Fact] // AC-30
    public async Task TheFixedFiltersOfTheToolAreIncludedInTheCount()
    {
        var live = await StartAsync(countedPoint: true, fixedFilters: new { nationality = American });
        await using var _ = live.Setup;

        var result = ResultOf(await AskAsync(live.Setup, Call(new { operation = "count", audience = ItAudience })));

        // c2 is Italian, c3 American: only c3 is for it and from the US.
        Assert.Contains("1 document in contracts", result, StringComparison.Ordinal);
    }

    [Fact] // AC-31
    public async Task ValuesWithAFilterAreWorkedOutNowAmongTheMatchingDocumentsAndCountDocuments()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var events = await AskAsync(live.Setup, Call(new { operation = "values", lookup = "supplier", audience = Managers }));

        var result = ResultOf(events);
        Assert.Equal("succeeded", StatusOf(events));
        Assert.Contains("supplier: 2 values, counting documents (from contracts)", result, StringComparison.Ordinal);
        Assert.Contains("Worked out now among the documents that match the filters", result, StringComparison.Ordinal);
        Assert.Contains("Globex - 2", result, StringComparison.Ordinal);
        Assert.Contains("Initech - 1", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme", result, StringComparison.Ordinal);
        Assert.DoesNotContain("As of", result, StringComparison.Ordinal);
    }

    [Fact] // AC-31
    public async Task AnEmptyFilterIsNoFilterSoTheStoredListIsRead()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var events = await AskAsync(live.Setup, Call(new { lookup = "supplier", audience = Array.Empty<string>() }));

        // The stored list of the supplier is still being prepared in this test, which proves it was read and not worked out.
        Assert.Equal("failed", StatusOf(events));
        Assert.Contains("being prepared", ResultOf(events), StringComparison.Ordinal);
    }

    [Fact] // AC-31
    public async Task ValuesWithAFilterOfAManualListAreAnErrorResult()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var events = await AskAsync(live.Setup, Call(new { lookup = "nationality", audience = Managers }));

        Assert.Equal("failed", StatusOf(events));
        Assert.Contains("cannot be filtered", ResultOf(events), StringComparison.Ordinal);
    }

    [Fact] // AC-31
    public async Task ASearchAndASortApplyToTheValuesWorkedOutNow()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var result = ResultOf(await AskAsync(live.Setup, Call(new { lookup = "supplier", audience = Managers, sort = "name", search = "INIT" })));

        Assert.Contains("supplier: 1 value", result, StringComparison.Ordinal);
        Assert.Contains("Initech - 1", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Globex", result, StringComparison.Ordinal);
    }

    [Fact] // AC-27
    public async Task AFilterThatTheToolDoesNotExposeIsAnErrorResult()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var events = await AskAsync(live.Setup, Call(new { operation = "count", supplier = AcmeOnly }));

        Assert.Equal("failed", StatusOf(events));
    }

    [Fact] // AC-32
    public async Task AFilterOnAFieldWithoutAnIndexWarnsWhenTheToolIsSaved()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var knowledgeBase = await setup.RegisterAsync("contracts", SampleCollections.CreateLookupsUnindexed, Mapping(countedPoint: false, listable: false), ContractFields);
        var fields = await setup.FieldsAsync();
        var manual = await LookupApi.CreateManualOkAsync(setup.Admin);

        var created = await LookupToolApi.CreateOkAsync(
            setup.Admin,
            LookupToolApi.Body(
                lookups: [LookupToolApi.Bind(manual)],
                knowledgeBaseIds: [knowledgeBase],
                exposedFields: [new { fieldId = fields["audience"] }]));

        var warning = Assert.Single(created.Warnings, w => w.Code == "lookup_tool.filter_unindexed");
        Assert.Contains("audience", warning.Message, StringComparison.Ordinal);
        Assert.Contains("contracts", warning.Message, StringComparison.Ordinal);
        var detail = await LookupToolApi.GetAsync(setup.Admin, created.Id);
        Assert.Contains(detail.Warnings, w => w.Code == "lookup_tool.filter_unindexed");
    }

    [Fact] // AC-32
    public async Task AFilterOnAnIndexedFieldDoesNotWarn()
    {
        var live = await StartAsync();
        await using var _ = live.Setup;

        var detail = await LookupToolApi.GetAsync(live.Setup.Admin, live.Tool);

        Assert.DoesNotContain(detail.Warnings, w => w.Code == "lookup_tool.filter_unindexed");
    }

    [Fact] // AC-32
    public async Task ACallThatRunsPastTheLimitEndsWithAnErrorThatSuggestsANarrowerFilter()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var live = await StartAsync(clock: clock);
        await using var _ = live.Setup;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        live.Setup.App.Qdrant.Hold = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        var asking = AskAsync(live.Setup, Call(new { operation = "count", audience = ItAudience }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        clock.Advance(TimeSpan.FromSeconds(16));
        var events = await asking;

        Assert.Equal("failed", StatusOf(events));
        Assert.Contains("15 seconds", ResultOf(events), StringComparison.Ordinal);
        Assert.Contains("narrower filter", ResultOf(events), StringComparison.Ordinal);
    }
}
