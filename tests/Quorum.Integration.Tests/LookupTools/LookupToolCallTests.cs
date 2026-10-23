using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Lookups;
using Quorum.Integration.Tests.Rag;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.SampleKnowledgeBase;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.LookupTools;

/// <summary>A lookup tool in an answer: the model calls it and reads a page of a list (spec 007, AC-10, AC-19, AC-22, AC-26, AC-28, AC-33, AC-35).</summary>
public sealed partial class LookupToolCallTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static FakeToolCall Call(object arguments, string id = "call_1", string tool = "lookup__lists") =>
        new(tool, JsonSerializer.Serialize(arguments), id);

    private static string ResultOf(IEnumerable<SseEvent> events, int index = 0) =>
        events.Where(e => e.Type == "tool_result").ElementAt(index).Data.GetProperty("content").GetString()!;

    private static string StatusOf(IEnumerable<SseEvent> events, int index = 0) =>
        events.Where(e => e.Type == "tool_result").ElementAt(index).Data.GetProperty("status").GetString()!;

    [GeneratedRegex("cursor \"(?<cursor>[^\"]+)\"")]
    private static partial Regex CursorText();

    private static string CursorOf(string result) => CursorText().Match(result).Groups["cursor"].Value;

    private static async Task<IReadOnlyList<SseEvent>> AskAsync(RagApi.Setup setup, params FakeToolCall[] calls)
    {
        setup.App.ModelServer.EnqueueRounds([.. calls.Select(c => ChatScript.CallTools(c)), ChatScript.Reply("Done.")]);
        var id = await ChatApi.CreateConversationAsync(setup.Admin);
        return await ChatApi.SendAsync(setup.Admin, id, "Question");
    }

    private async Task<(RagApi.Setup Setup, Guid Tool)> StartWithManualAsync(object[]? values = null, string name = "nationality")
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var lookup = await LookupApi.CreateManualOkAsync(setup.Admin, LookupApi.ManualBody(name: name, values: values));
        var tool = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup)]));
        await ToolFixtures.ActivateOnlyAsync(setup.Admin, "lookup__lists");
        return (setup, tool.Id);
    }

    [Fact] // AC-10
    public async Task TheModelSeesAManualValueWithItsLabelAndSynonymsAndASearchForASynonymFindsIt()
    {
        var (setup, _) = await StartWithManualAsync();
        await using var _ = setup;

        var events = await AskAsync(setup, Call(new { search = "italiana" }));

        var result = ResultOf(events);
        Assert.Equal("succeeded", StatusOf(events));
        Assert.Contains("IT (Italy, Italia, italiana)", result, StringComparison.Ordinal);
        Assert.DoesNotContain("FR", result, StringComparison.Ordinal);
    }

    [Fact] // AC-28
    public async Task AValuesResultSaysWhatTheListIsHowManyValuesItHasAndWhenItWasComputed()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await using var _ = setup;
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        await LookupToolApi.PublishAsync(setup.App, lookup, [("legal", 4), ("hr", 9), ("finance", 4)]);
        await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup)]));
        await ToolFixtures.ActivateOnlyAsync(setup.Admin, "lookup__lists");

        var result = ResultOf(await AskAsync(setup, Call(new { lookup = "department" })));

        Assert.Contains("department: 3 values, counting documents (from policies)", result, StringComparison.Ordinal);
        Assert.Contains("As of 2026-10-22 12:00 UTC", result, StringComparison.Ordinal);
        Assert.DoesNotContain("out of date", result, StringComparison.Ordinal);
        Assert.DoesNotContain("incomplete", result, StringComparison.Ordinal);
        // By count and then by value, as the list was ranked.
        var rows = Rows(result);
        Assert.Equal(["hr - 9", "finance - 4", "legal - 4"], rows);
    }

    [Fact] // AC-19
    public async Task AListThatHasNoGenerationYetIsAnErrorResultThatSaysItIsBeingPrepared()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await using var _ = setup;
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup)]));
        await ToolFixtures.ActivateOnlyAsync(setup.Admin, "lookup__lists");

        var events = await AskAsync(setup, Call(new { }));

        Assert.Equal("failed", StatusOf(events));
        Assert.Contains("being prepared", ResultOf(events), StringComparison.Ordinal);
    }

    [Fact] // AC-33
    public async Task AnUnknownListAnUnknownSortAndALimitOutOfRangeAreErrorResults()
    {
        var (setup, _) = await StartWithManualAsync();
        await using var _ = setup;

        var events = await AskAsync(
            setup,
            Call(new { lookup = "colours" }, "call_1"),
            Call(new { sort = "size" }, "call_2"),
            Call(new { limit = 500 }, "call_3"),
            Call(new { operation = "documents" }, "call_4"));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("failed", StatusOf(events, i));
        }

        // The enums of the schema refuse them before the tool runs; the checks of the tool itself are a second line.
        Assert.Contains("not valid", ResultOf(events, 0), StringComparison.Ordinal);
        Assert.Contains("lookup", ResultOf(events, 0), StringComparison.Ordinal);
    }

    [Fact] // AC-28
    public async Task APageIsCutToWholeRowsThatFitTheResultSizeAndItsCursorContinuesFromTheLastRowShown()
    {
        var values = Enumerable.Range(0, 300).Select(i => LookupApi.Value($"value-{i:D3}-{new string('x', 40)}")).ToArray();
        var (setup, _) = await StartWithManualAsync(values);
        await using var _ = setup;

        var first = ResultOf(await AskAsync(setup, Call(new { limit = 200 })));
        var firstRows = Rows(first);
        var next = ResultOf(await AskAsync(setup, Call(new { limit = 200, cursor = CursorOf(first) })));
        var nextRows = Rows(next);

        Assert.True(first.Length <= 6_000, $"{first.Length} characters");
        Assert.InRange(firstRows.Count, 10, 199);
        Assert.StartsWith("value-000", firstRows[0], StringComparison.Ordinal);
        Assert.StartsWith($"value-{firstRows.Count:D3}", nextRows[0], StringComparison.Ordinal);
        Assert.Contains("There are more values", first, StringComparison.Ordinal);
    }

    [Fact] // AC-35
    public async Task EveryCallIsAuditedWithTheNumberOfValuesItReturned()
    {
        var (setup, _) = await StartWithManualAsync();
        await using var _ = setup;

        await AskAsync(setup, Call(new { }));

        var entry = Assert.Single(await ToolFixtures.AuditRowsAsync(setup.App));
        Assert.Equal("lists", entry.ToolName);
        Assert.Equal("lookup", entry.ServerName);
        Assert.Equal("Succeeded", entry.Outcome);
        Assert.Equal(2, entry.SourceCount);
    }

    [Fact] // AC-22
    public async Task AToolWithFixedFiltersReadsTheListOfItsOwnSlice()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await using var _ = setup;
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        await LookupToolApi.PublishAsync(setup.App, lookup, [("legal", 4), ("hr", 9), ("finance", 4)]);
        await LookupToolApi.CreateOkAsync(
            setup.Admin,
            LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup)], fixedFilters: new Dictionary<string, object> { ["year_min"] = 2024 }));
        await ToolFixtures.ActivateOnlyAsync(setup.Admin, "lookup__lists");
        var sliceHash = (await LookupToolApi.SlicesAsync(setup.App, lookup)).Single(s => !s.IsWholeKnowledgeBase).Hash;

        var beforeComputation = await AskAsync(setup, Call(new { }));
        await LookupToolApi.PublishAsync(setup.App, lookup, [("hr", 2)], sliceHash);
        var afterComputation = await AskAsync(setup, Call(new { }));

        // The list of the whole knowledge base has three values, but this tool never sees them (AC-23: it waits for its own).
        Assert.Contains("being prepared", ResultOf(beforeComputation), StringComparison.Ordinal);
        Assert.Equal(["hr - 2"], Rows(ResultOf(afterComputation)));
    }

    [Fact] // AC-26
    public async Task AKnowledgeBaseThatDoesNotMapAFieldOfTheFiltersIsSkippedAndNamed()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await using var _ = setup;
        var (_, withYear) = await LookupToolApi.RegisterWithLookupAsync(setup, "with-year");
        var (_, withoutYear) = await LookupToolApi.RegisterWithLookupAsync(setup, "without-year", LookupToolApi.Mapping(("department", "department", true)));
        await LookupToolApi.CreateOkAsync(
            setup.Admin,
            LookupToolApi.Body(
                lookups: [LookupToolApi.Bind(withYear, "department"), LookupToolApi.Bind(withoutYear, "other_department")],
                fixedFilters: new Dictionary<string, object> { ["year_min"] = 2024 }));
        await ToolFixtures.ActivateOnlyAsync(setup.Admin, "lookup__lists");

        var events = await AskAsync(setup, Call(new { lookup = "other_department" }));

        Assert.Equal("failed", StatusOf(events));
        var result = ResultOf(events);
        Assert.Contains("without-year", result, StringComparison.Ordinal);
        Assert.Contains("skipped", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolIsOnlyOfferedWhileOneOfItsListsIsVisible()
    {
        var (setup, tool) = await StartWithManualAsync();
        await using var _ = setup;

        var pool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool;

        var offered = Assert.Single(pool, t => t.ToolId == tool);
        Assert.Equal("lookup__lists", offered.Name);
        Assert.True(offered.Active);
    }

    private static readonly object[] SupplierFields =
    [
        new { name = "country", type = "Text" },
        new { name = "contract_count", type = "Number" },
        new { name = "supplier_since", type = "Date" },
    ];

    private static readonly string[] SupplierAttributes = ["country", "contract_count", "supplier_since"];

    [Fact]
    public async Task AnEntityListIsOrderedByTheAttributeTheModelNamesAndATextAttributeIsRefused()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        await using var _ = setup;
        setup.App.LookupComputer.Handler = new LookupComputer(setup.App.Services.GetRequiredService<StoreAdapters>()).ComputeAsync;
        var knowledgeBase = await setup.RegisterAsync(
            "suppliers",
            SampleCollections.CreateSuppliers,
            new
            {
                roles = new { text = "text" },
                fields = SupplierAttributes.Select(name => new { path = name, name, entityAttribute = true }).ToArray(),
                entityName = "name",
            },
            SupplierFields);
        var lookups = await LookupApi.UntilAsync(
            async () => (await setup.Admin.GetFromJsonAsync<List<JsonElement>>(LookupApi.KnowledgeBaseLookups(knowledgeBase), TestContext.Current.CancellationToken))!,
            list => list.Count > 0 && list.All(l => l.GetProperty("slices").EnumerateArray().All(s => s.GetProperty("status").GetString() == "Ready" && !s.GetProperty("running").GetBoolean())));
        var fields = await setup.FieldsAsync();
        var tool = await LookupToolApi.CreateOkAsync(
            setup.Admin,
            LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookups.Single().GetProperty("id").GetGuid())], exposedFields: [new { fieldId = fields["country"] }]));
        await ToolFixtures.ActivateOnlyAsync(setup.Admin, "lookup__lists");

        var definition = (await LookupToolApi.GetAsync(setup.Admin, tool.Id)).Definition!;
        Assert.Contains("contract_count", definition.Parameter("sort").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("country", definition.Parameter("sort").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));

        var ordered = ResultOf(await AskAsync(setup, Call(new { sort = "-contract_count" })));
        Assert.Contains("Ordered by contract_count, the largest first.", ordered, StringComparison.Ordinal);
        Assert.Equal(["Acme", "Hooli", "Globex", "Initech"], Rows(ordered).Select(r => r.Split(' ')[0]));

        var events = await AskAsync(setup, Call(new { sort = "country" }));
        Assert.Equal("failed", StatusOf(events));
        // A text attribute is not in the enum, so the schema refuses it before the tool runs.
        Assert.Contains("not valid", ResultOf(events), StringComparison.Ordinal);

        // A list of entities cannot be filtered, so the fields only its knowledge base maps are not offered as filters: nothing would apply them.
        Assert.DoesNotContain("country", definition.Parameters);
    }

    private static List<string> Rows(string result)
    {
        var lines = result.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("<<<VALUES-", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, l => l.StartsWith("<<<END-VALUES-", StringComparison.Ordinal));
        return start < 0 || end < 0 ? [] : [.. lines[(start + 1)..end]];
    }
}
