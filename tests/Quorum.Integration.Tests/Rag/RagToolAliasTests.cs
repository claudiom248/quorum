using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.SampleKnowledgeBase;
using static Quorum.Integration.Tests.Fixtures.FakeModelServer;

namespace Quorum.Integration.Tests.Rag;

/// <summary>Aliases of the exposed fields of a rag tool, over fields identified by cluster and name (spec 003, Amendment 1, AC-54 to AC-57).</summary>
public sealed class RagToolAliasTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static readonly string[] Year2024 = ["2024"];

    private static string[] Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];

    // The flat sample with `year` written as text, as an ingestion that keeps it as it was typed would leave it.
    private static SampleDataset TextYear(string name)
    {
        var flat = SampleCollections.CreateFlat(name);
        return flat with
        {
            Indexes = [.. flat.Indexes.Where(i => i.Path != "year")],
            Points = [.. flat.Points.Select(p => p with { Payload = TextYearPayload(p.Payload) })],
        };
    }

    private static JsonObject TextYearPayload(JsonObject payload)
    {
        var copy = (JsonObject)payload.DeepClone();
        copy["year"] = copy["year"]!.ToString();
        return copy;
    }

    private static object Mapping(string cluster) => new
    {
        roles = new { text = "text", documentId = "document_id", title = "title", position = "page", sourceUrl = "source_url" },
        fields = new[] { new { path = "year", cluster, name = "year" } },
    };

    private sealed record Fixture(RagApi.Setup Setup, Guid HrYear, Guid FinanceYear, Guid HrBase, Guid FinanceBase);

    /// <summary>Two knowledge bases that both map a field called year: text in cluster hr, a number in cluster finance.</summary>
    private async Task<Fixture> StartAsync()
    {
        var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var hrYear = await DictionaryApi.CreateOkAsync(setup.Admin, "year", "Text", cluster: "hr");
        var financeYear = await DictionaryApi.CreateOkAsync(setup.Admin, "year", "Number", cluster: "finance");
        var hrBase = await setup.RegisterAsync("hr_docs", TextYear, Mapping("hr"), createFields: []);
        var financeBase = await setup.RegisterAsync("finance_docs", mapping: Mapping("finance"), createFields: []);
        return new Fixture(setup, hrYear, financeYear, hrBase, financeBase);
    }

    private static object[] Exposed(Fixture f, string? hrAlias, string? financeAlias) =>
    [
        new { fieldId = f.HrYear, alias = hrAlias },
        new { fieldId = f.FinanceYear, alias = financeAlias },
    ];

    private static Task<string?> CodeAsync(HttpResponseMessage response) => KnowledgeBaseApi.ProblemCodeAsync(response);

    [Fact] // AC-54
    public async Task TwoFieldsCalledYearAreExposedUnderTheirOwnAliasesWithTheShapesOfTheirTypes()
    {
        var f = await StartAsync();
        await using var setup = f.Setup;

        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [f.HrBase, f.FinanceBase], exposedFields: Exposed(f, "year_hr", "year_finance")));

        Assert.Equal(["knowledge_base", "query", "year_finance_max", "year_finance_min", "year_hr"], Sorted(created.Definition!.Parameters));
        Assert.Equal("array", created.Definition.Parameter("year_hr").GetProperty("type").GetString());
        Assert.Equal("number", created.Definition.Parameter("year_finance_min").GetProperty("type").GetString());
        Assert.StartsWith("year (hr)", created.Definition.Parameter("year_hr").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("year (finance)", created.Definition.Parameter("year_finance_min").GetProperty("description").GetString(), StringComparison.Ordinal);

        var detail = await RagApi.GetAsync(setup.Admin, created.Id);
        Assert.Equal(
            new[] { (f.HrYear, "year_hr"), (f.FinanceYear, "year_finance") }.Order().ToArray(),
            detail.ExposedFields.Select(e => (e.FieldId, e.Alias!)).Order().ToArray());
    }

    [Fact] // AC-54, D-12
    public async Task AFilterOnAnAliasSkipsTheKnowledgeBaseThatDoesNotMapThatField()
    {
        var f = await StartAsync();
        await using var setup = f.Setup;
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [f.HrBase, f.FinanceBase], exposedFields: Exposed(f, "year_hr", "year_finance"), topK: 20));
        await RagApi.ActivateAsync(setup.Admin, "rag__company");
        var year = SampleCollections.CreateFlat().Points[0].Payload["year"]!.ToString();

        var byText = await AskAsync(setup, new { query = "policy", year_hr = new[] { year } });
        var byRange = await AskAsync(setup, new { query = "policy", year_finance_min = 1900 }, "call_2");

        Assert.Contains("Not searched, because they have no field for the filters used: finance_docs.", ResultOf(byText), StringComparison.Ordinal);
        Assert.Contains("Not searched, because they have no field for the filters used: hr_docs.", ResultOf(byRange), StringComparison.Ordinal);
        Assert.Contains("finance_docs ·", ResultOf(byRange), StringComparison.Ordinal);
    }

    [Fact] // AC-55
    public async Task TwoFieldsWithTheSameAliasAreRefused()
    {
        var f = await StartAsync();
        await using var setup = f.Setup;

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [f.HrBase, f.FinanceBase], exposedFields: Exposed(f, "same", "same")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.alias_conflict", await CodeAsync(response));
    }

    [Fact] // AC-55
    public async Task WithoutAliasesTwoFieldsCalledYearCollideAndOneOfThemMustBeRenamed()
    {
        var f = await StartAsync();
        await using var setup = f.Setup;

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [f.HrBase, f.FinanceBase], exposedFields: Exposed(f, null, null)));

        Assert.Equal("rag.alias_conflict", await CodeAsync(response));
    }

    [Theory] // AC-55
    [InlineData("Year")]
    [InlineData("query")]
    [InlineData("knowledge_base")]
    [InlineData("price_min")]
    [InlineData("1st")]
    public async Task AnAliasThatIsNotAValidNameOrIsReservedIsRefused(string alias)
    {
        var f = await StartAsync();
        await using var setup = f.Setup;

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [f.HrBase], exposedFields: [new { fieldId = f.HrYear, alias }]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.invalid_alias", await CodeAsync(response));
    }

    [Fact] // AC-56
    public async Task AToolWithNoAliasUsesTheNamesOfItsFields()
    {
        var f = await StartAsync();
        await using var setup = f.Setup;

        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(name: "finance", knowledgeBaseIds: [f.FinanceBase], exposedFields: [new { fieldId = f.FinanceYear }]));

        Assert.Equal(["query", "year_max", "year_min"], Sorted(created.Definition!.Parameters));
        var detail = await RagApi.GetAsync(setup.Admin, created.Id);
        Assert.Equal("year", Assert.Single(detail.ExposedFields).Alias);
    }

    [Fact] // AC-57
    public async Task AFixedFilterOnANameSeveralClustersHaveIsRefusedUntilOneIsExposedUnderAnAlias()
    {
        var f = await StartAsync();
        await using var setup = f.Setup;
        var ambiguous = new { year = Year2024 };

        using var refused = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [f.HrBase, f.FinanceBase], exposedFields: [], fixedFilters: ambiguous));
        var accepted = await RagApi.CreateOkAsync(
            setup.Admin,
            RagApi.Body(
                knowledgeBaseIds: [f.HrBase, f.FinanceBase],
                exposedFields: [new { fieldId = f.HrYear, alias = "year_hr" }],
                fixedFilters: new { year_hr = Year2024 }));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("rag.invalid_filter", await CodeAsync(refused));
        Assert.Contains("several clusters", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, accepted.Id);
    }

    [Fact] // AC-57
    public async Task AFixedFilterMayNameAnUnexposedFieldByItsNameWhenNoOtherHasIt()
    {
        var f = await StartAsync();
        await using var setup = f.Setup;

        var created = await RagApi.CreateOkAsync(
            setup.Admin,
            RagApi.Body(knowledgeBaseIds: [f.FinanceBase], exposedFields: [], fixedFilters: new { year_min = 1900 }));

        Assert.Equal(["query"], Sorted(created.Definition!.Parameters));
    }

    private static async Task<IReadOnlyList<SseEvent>> AskAsync(RagApi.Setup setup, object arguments, string id = "call_1")
    {
        setup.App.ModelServer.EnqueueRounds([ChatScript.CallTools(new FakeToolCall("rag__company", JsonSerializer.Serialize(arguments), id)), ChatScript.Reply("Done.")]);
        var conversation = await ChatApi.CreateConversationAsync(setup.Admin);
        return await ChatApi.SendAsync(setup.Admin, conversation, "Question");
    }

    private static string ResultOf(IEnumerable<SseEvent> events) =>
        events.Where(e => e.Type == "tool_result").ElementAt(0).Data.GetProperty("content").GetString()!;
}
