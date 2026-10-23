using System.Text.Json;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The lookup members of the mapping draft and the manual lookup form (spec 007 AC-43), without any markup.</summary>
public sealed class LookupDraftTests
{
    private static readonly Guid DepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5001");
    private static readonly Guid YearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5002");

    private static readonly IReadOnlyList<DictionaryField> Dictionary =
    [
        new(DepartmentId, "department", DictionaryFieldTypes.Text, null, ScopeKinds.Tenant, 0),
        new(YearId, "year", DictionaryFieldTypes.Number, null, ScopeKinds.Tenant, 0),
    ];

    private static MappingDraft Saved(bool yearListable = false) => MappingDraft.From(new MappingInfo(
        new MappingRoles("text", null, null, null, null),
        [
            new MappedFieldInfo("department", DepartmentId, "department", Listable: true, EntityAttribute: true),
            new MappedFieldInfo("year", YearId, "year", Listable: yearListable),
        ],
        new CountedPointInfo("is_first_chunk", JsonSerializer.SerializeToElement(true)),
        "title"));

    [Fact]
    public void ASavedMappingKeepsItsLookupMembersAndSendsThemBack()
    {
        var (mapping, _) = Saved().ToRequest(Dictionary);

        Assert.Equal("title", mapping.EntityName);
        Assert.Equal("is_first_chunk", mapping.CountedPoint!.Path);
        Assert.Equal(JsonValueKind.True, mapping.CountedPoint.Value.ValueKind);
        Assert.Equal((true, true), (mapping.Fields[0].Listable, mapping.Fields[0].EntityAttribute));
        Assert.Equal((false, false), (mapping.Fields[1].Listable, mapping.Fields[1].EntityAttribute));
    }

    [Fact]
    public void TheDraftFromAnInspectionStartsWithTheProposedCountedPoint()
    {
        var draft = MappingDraft.From(new DraftMapping(
            new MappingRoles("text", null, null, null, null),
            [],
            new CountedPointInfo("chunk_index", JsonSerializer.SerializeToElement(0))));

        Assert.Equal(("chunk_index", "0"), (draft.CountedPointPath, draft.CountedPointValue));
        Assert.Equal(JsonValueKind.Number, draft.ToRequest(Dictionary).Mapping.CountedPoint!.Value.ValueKind);
    }

    [Theory]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("TRUE", JsonValueKind.True)]
    [InlineData("0", JsonValueKind.Number)]
    [InlineData("1.5", JsonValueKind.Number)]
    [InlineData("first", JsonValueKind.String)]
    [InlineData("\"0\"", JsonValueKind.String)]
    [InlineData("\"true\"", JsonValueKind.String)]
    public void TheCountedPointValueIsReadAsTheTypeItLooksLike(string typed, JsonValueKind expected)
    {
        var draft = Saved();
        draft.CountedPointValue = typed;

        Assert.Equal(expected, draft.CountedPoint()!.Value.ValueKind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("\"true\"")]
    [InlineData("first")]
    public void ACountedPointValueSurvivesBeingSavedAndLoadedAgain(string typed)
    {
        var draft = Saved();
        draft.CountedPointValue = typed;
        var sent = draft.CountedPoint()!;

        var again = MappingDraft.From(new MappingInfo(new MappingRoles("text", null, null, null, null), [], sent));

        Assert.Equal(typed, again.CountedPointValue);
    }

    [Fact]
    public void ACountedPointNeedsBothItsPathAndItsValue()
    {
        var draft = Saved();
        draft.CountedPointValue = string.Empty;

        Assert.Contains(draft.LookupProblems(Dictionary), p => p.Contains("needs both the payload path and the value", StringComparison.Ordinal));

        draft.CountedPointPath = string.Empty;
        Assert.DoesNotContain(draft.LookupProblems(Dictionary), p => p.Contains("counted point", StringComparison.Ordinal));
        Assert.Null(draft.CountedPoint());
    }

    [Fact]
    public void OnlyTextFieldsCanBeListed()
    {
        var draft = Saved(yearListable: true);

        var problem = Assert.Single(draft.LookupProblems(Dictionary));

        Assert.Contains("year", problem, StringComparison.Ordinal);
        Assert.Contains("only text and list of texts", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityAttributesNeedAnEntityNameAndAreLimitedToTen()
    {
        var draft = Saved();
        draft.EntityName = string.Empty;
        Assert.Contains(draft.LookupProblems(Dictionary), p => p.Contains("need the entity name", StringComparison.Ordinal));

        draft.EntityName = "title";
        for (var i = 0; i < 11; i++)
        {
            draft.AddRow().EntityAttribute = true;
        }

        Assert.Contains(draft.LookupProblems(Dictionary), p => p.Contains("up to 10 attributes", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEntityNameCannotBeMappedAsAFieldToo()
    {
        var draft = Saved();
        draft.Text = "text";
        draft.EntityName = "department";

        Assert.Contains(draft.Problems(Dictionary), p => p.Contains("\"department\" is mapped twice", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(168, true)]
    [InlineData(169, false)]
    public void TheRefreshIntervalIsOneToOneHundredSixtyEightHours(int hours, bool valid)
    {
        var draft = new KnowledgeBaseDraft { LookupRefreshHours = hours, Mapping = Saved() };

        Assert.Equal(valid, !draft.LookupProblems(Dictionary).Any(p => p.Contains("refresh interval", StringComparison.Ordinal)));
    }

    [Fact]
    public void ADraftFromADetailTakesTheRefreshIntervalAndOldDetailsReadAsTheDefault()
    {
        var detail = new KnowledgeBaseDetail(
            Guid.NewGuid(), "kb", "d", "Available", false, "Qdrant", new ConnectionInfo("http://q:6334", "c", null, false), "m", null, null, null, null, null);

        Assert.Equal(LookupLimits.DefaultRefreshHours, KnowledgeBaseDraft.From(detail).LookupRefreshHours);
        Assert.Equal(72, KnowledgeBaseDraft.From(detail with { LookupRefreshHours = 72 }).LookupRefreshHours);
    }

    [Fact]
    public void AnEntityListCanBeOrderedByItsNumberAndDateAttributesOnly()
    {
        LookupValueInfo Entity(string json) => new("e", null, null, null, JsonDocument.Parse(json).RootElement.Clone());

        var attributes = LookupAdminView.SortableAttributes(
        [
            Entity("""{"country":"IT","contract_count":12,"supplier_since":"2019-03-01","code":"1"}"""),
            Entity("""{"country":"US","contract_count":5,"supplier_since":"2021-06-15","code":"2"}"""),
        ]);

        Assert.Equal(["contract_count", "supplier_since"], attributes);
    }

    [Fact]
    public void TheAttributesOfAnEntityReadAsTheyAreInTheData()
    {
        var value = new LookupValueInfo("Acme", null, null, null, JsonDocument.Parse("""{"country":"IT","rating":4.4,"preferred":true,"supplier_since":"2019-03-01"}""").RootElement.Clone());

        Assert.Equal("country: IT, rating: 4.4, preferred: true, supplier_since: 2019-03-01", LookupAdminView.Attributes(value));
    }

    // --- Manual lookup form ---

    [Fact]
    public void TheValuesAreOnePerLineWithAnOptionalLabelAndSynonyms()
    {
        var form = new ManualLookupForm { ValuesText = "IT | Italian | Italy; Italia\r\nFR | French\n\n  DE  \n" };

        var (values, problems) = form.ParseValues();

        Assert.Empty(problems);
        Assert.Equal(["IT", "FR", "DE"], values.Select(v => v.Value));
        Assert.Equal("Italian", values[0].Label);
        Assert.Equal(["Italy", "Italia"], values[0].Synonyms);
        Assert.Equal("French", values[1].Label);
        Assert.Null(values[1].Synonyms);
        Assert.Null(values[2].Label);
        Assert.Null(values[2].Synonyms);
    }

    [Fact]
    public void AProblemNamesTheLineItIsOn()
    {
        var form = new ManualLookupForm { ValuesText = "a\n\n | label only\nb\nB" };

        var (_, problems) = form.ParseValues();

        Assert.Equal(["Line 3: the value is empty.", "Line 5: \"B\" is listed twice, whatever its case."], problems);
    }

    [Fact]
    public void MoreThanFiveThousandValuesAreRefused()
    {
        var form = new ManualLookupForm { ValuesText = string.Join('\n', Enumerable.Range(0, LookupLimits.MaxManualValues + 1)) };

        Assert.Contains(form.ParseValues().Problems, p => p.Contains("at most 5000 values", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("nationality", true)]
    [InlineData("Nationality", false)]
    [InlineData("1abc", false)]
    [InlineData("", false)]
    public void TheNameFollowsTheRulesOfADictionaryName(string name, bool valid)
    {
        var form = new ManualLookupForm { Name = name, ValuesText = "a" };

        Assert.Equal(valid, form.Problems().Count == 0);
    }

    [Fact]
    public void ACsvAddsValuesLabelsAndSynonymsAfterWhatIsAlreadyThere()
    {
        var form = new ManualLookupForm { ValuesText = "XX" };

        var added = form.ImportCsv("value,label,synonyms\r\nIT,Italian,Italy;Italia\r\n\"FR\",\"French, as spoken\",\r\nDE,,\r\n,,\r\n");

        Assert.Equal(3, added);
        var (values, problems) = form.ParseValues();
        Assert.Empty(problems);
        Assert.Equal(["XX", "IT", "FR", "DE"], values.Select(v => v.Value));
        Assert.Equal(["Italy", "Italia"], values[1].Synonyms);
        Assert.Equal("French, as spoken", values[2].Label);
    }

    [Fact]
    public void ASemicolonCsvAndAQuotedQuoteAreRead()
    {
        var form = new ManualLookupForm();

        form.ImportCsv("IT;\"The \"\"boot\"\"\";Italy|Italia\n");

        var (values, _) = form.ParseValues();
        var only = Assert.Single(values);
        Assert.Equal(("IT", "The \"boot\""), (only.Value, only.Label));
        Assert.Equal(["Italy", "Italia"], only.Synonyms);
    }

    [Fact]
    public void AGlobalScopeSendsNoTenantsAndATenantScopeSendsTheOnesTyped()
    {
        var tenant = Guid.NewGuid();
        var form = new ManualLookupForm { Name = "audience", ValuesText = "a", TenantsText = tenant.ToString() };

        Assert.Equal([tenant], form.ToRequest().ScopeTenantIds);

        form.Scope = ScopeKinds.Global;
        Assert.Empty(form.ToRequest().ScopeTenantIds!);

        form.Scope = ScopeKinds.Tenant;
        form.TenantsText = "not-an-id";
        Assert.Contains(form.Problems(), p => p.Contains("is not an id", StringComparison.Ordinal));
    }

    [Fact]
    public void ALookupSavedByTheServerIsEditedFromItsValuesInOrder()
    {
        var info = new ManualLookupInfo(Guid.NewGuid(), "nationality", "n", ScopeKinds.Tenant, 2, false, []);

        var form = ManualLookupForm.From(info, [new LookupValueInfo("IT", "Italian", ["Italy"], null, null), new LookupValueInfo("FR", null, null, null, null)]);

        Assert.Equal("IT | Italian | Italy\nFR", form.ValuesText.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.False(form.IsNew);
    }
}
