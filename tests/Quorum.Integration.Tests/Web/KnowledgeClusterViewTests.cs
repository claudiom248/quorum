using System.Text.Json;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The logic behind clusters and aliases in the knowledge admin pages (spec 003 Amendment 1, AC-52 to AC-58).</summary>
public sealed class KnowledgeClusterViewTests
{
    private static readonly Guid KnowledgeBaseId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5011");
    private static readonly Guid HrDepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5101");
    private static readonly Guid LegalDepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5102");
    private static readonly Guid HrYearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5103");
    private static readonly Guid FinanceYearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5104");

    private static readonly DictionaryField HrDepartment = new(HrDepartmentId, "department", DictionaryFieldTypes.Text, null, new ToolScope(ScopeKinds.Tenant, [], []), 0, "hr");
    private static readonly DictionaryField LegalDepartment = new(LegalDepartmentId, "department", DictionaryFieldTypes.Text, null, new ToolScope(ScopeKinds.Tenant, [], []), 0, "legal");
    private static readonly DictionaryField HrYear = new(HrYearId, "year", DictionaryFieldTypes.Text, null, new ToolScope(ScopeKinds.Tenant, [], []), 0, "hr");
    private static readonly DictionaryField FinanceYear = new(FinanceYearId, "year", DictionaryFieldTypes.Number, null, new ToolScope(ScopeKinds.Tenant, [], []), 0, "finance");
    private static readonly DictionaryField NoClusterYear = new(Guid.NewGuid(), "year", DictionaryFieldTypes.Number, null, new ToolScope(ScopeKinds.Tenant, [], []), 0);
    private static readonly DictionaryField[] Clustered = [HrDepartment, LegalDepartment, HrYear, FinanceYear];

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DraftMapping DraftWith(params DraftField[] fields) => new(new MappingRoles("text", null, null, null, null), fields);

    private static FieldCandidate[] DepartmentCandidates() =>
        [new FieldCandidate(HrDepartmentId, "hr", DictionaryFieldTypes.Text), new FieldCandidate(LegalDepartmentId, "legal", DictionaryFieldTypes.Text)];

    // --- Mapping ---

    [Fact] // AC-53
    public void ANameSeveralClustersHaveIsNotPreselectedAndBlocksUntilTheAdminChooses()
    {
        var candidates = DepartmentCandidates();
        var draft = MappingDraft.From(DraftWith(new DraftField("department", "department", DictionaryFieldTypes.Text, null, [], null, candidates)));

        var before = draft.Review(Clustered).Single();
        Assert.Contains("clusters: hr, legal", Assert.Single(before.Problems), StringComparison.Ordinal);
        Assert.Empty(draft.NewFields(Clustered));

        draft.Rows[0].Pick(candidates[1]);

        var after = draft.Review(Clustered).Single();
        Assert.Empty(after.Problems);
        Assert.Equal(LegalDepartmentId, after.Existing!.Id);
        Assert.Equal(LegalDepartmentId, draft.ToRequest(Clustered).Mapping.Fields.Single().DictionaryFieldId);
    }

    [Fact] // AC-53
    public void TypingAClusterCountsAsAChoiceAndAnUnknownPairIsANewField()
    {
        var draft = MappingDraft.From(DraftWith(new DraftField("department", "department", DictionaryFieldTypes.Text, null, [], null, DepartmentCandidates())));

        draft.Rows[0].Cluster = "operations";
        draft.ConfirmCreate = true;

        var (mapping, create) = draft.ToRequest(Clustered);
        Assert.Empty(draft.Problems(Clustered));
        Assert.Equal(new CreateFieldRequest("department", DictionaryFieldTypes.Text, Cluster: "operations"), Assert.Single(create));
        Assert.Equal(("department", "operations", (Guid?)null), (mapping.Fields[0].Name, mapping.Fields[0].Cluster, mapping.Fields[0].DictionaryFieldId));
    }

    [Fact] // AC-52
    public void TheSameNameInTwoClustersIsTwoFieldsAndTheSameClusterAndNameTwiceIsAClash()
    {
        var draft = MappingDraft.From(DraftWith(
            new DraftField("a.year", "year", DictionaryFieldTypes.Text, null, []),
            new DraftField("b.year", "year", DictionaryFieldTypes.Number, null, [])));
        draft.Rows[0].Cluster = "hr";
        draft.Rows[1].Cluster = "finance";
        draft.ConfirmCreate = true;

        Assert.Empty(draft.Problems([]));
        Assert.Equal(["hr/year", "finance/year"], draft.NewFields([]).Select(f => $"{f.Cluster}/{f.Name}"));

        draft.Rows[1].Cluster = "hr";

        Assert.Contains(draft.Problems([]), p => p.Contains("also uses \"hr/year\"", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExistingFieldIsFoundByClusterAndNameAndAConflictIsAboutThatField()
    {
        var draft = MappingDraft.From(DraftWith(new DraftField("year", "year", DictionaryFieldTypes.Text, null, [])));
        draft.Rows[0].Cluster = "finance";

        var review = draft.Review(Clustered).Single();

        Assert.Equal(FinanceYearId, review.Existing!.Id);
        Assert.True(review.HasTypeConflict);
        Assert.Contains("another cluster", Assert.Single(review.Problems), StringComparison.Ordinal);

        draft.Rows[0].Cluster = "hr";

        var moved = draft.Review(Clustered).Single();
        Assert.Empty(moved.Problems);
        Assert.Equal(HrYearId, moved.Existing!.Id);
    }

    [Fact]
    public void AFieldWithNoClusterIsNotConfusedWithOneInACluster()
    {
        var draft = MappingDraft.From(DraftWith(new DraftField("year", "year", DictionaryFieldTypes.Number, null, [])));

        Assert.Equal(NoClusterYear.Id, draft.Review([.. Clustered, NoClusterYear]).Single().Existing!.Id);
        Assert.True(draft.Review(Clustered).Single().IsNew);
    }

    [Fact]
    public void AClusterThatIsNotAValidNameIsFlaggedOnANewField()
    {
        var draft = MappingDraft.From(DraftWith(new DraftField("x", "x", DictionaryFieldTypes.Text, null, [])));
        draft.Rows[0].Cluster = "Not Valid";

        Assert.Contains("not a valid cluster", Assert.Single(draft.Review([]).Single().Problems), StringComparison.Ordinal);
    }

    [Fact] // AC-58
    public void ASavedMappingKeepsTheClusterOfItsFields()
    {
        var draft = MappingDraft.From(new MappingInfo(
            new MappingRoles("text", null, null, null, null),
            [new MappedFieldInfo("year", HrYearId, "year", "hr")]));

        Assert.Equal(HrYearId, draft.Review(Clustered).Single().Existing!.Id);
    }

    // --- Presentation ---

    [Fact] // AC-58
    public void AClusterIsWrittenInFrontOfANameOnlyWhereTheNameAloneIsAmbiguous()
    {
        var region = new DictionaryField(Guid.NewGuid(), "region", DictionaryFieldTypes.Text, null, new ToolScope(ScopeKinds.Tenant, [], []), 0, "sales");
        DictionaryField[] all = [.. Clustered, region];

        Assert.Equal("hr/year", KnowledgeAdminView.FieldLabel(HrYear, all));
        Assert.Equal("finance/year", KnowledgeAdminView.FieldLabel(FinanceYear, all));
        Assert.Equal("region", KnowledgeAdminView.FieldLabel(region, all));
        Assert.Equal("year", KnowledgeAdminView.FieldLabel(NoClusterYear, all));
    }

    [Fact] // AC-58
    public void FieldsAreGroupedByClusterWithTheOnesWithNoClusterFirst()
    {
        var groups = KnowledgeAdminView.GroupByCluster([FinanceYear, NoClusterYear, HrYear, HrDepartment]);

        Assert.Equal([null, "finance", "hr"], groups.Select(g => g.Cluster));
        Assert.Equal(["department", "year"], groups[2].Fields.Select(f => f.Name));
    }

    // --- Rag tool aliases ---

    private static RagToolForm ClusteredRagForm()
    {
        var form = new RagToolForm { Name = "company", Description = "Searches the company." };
        form.KnowledgeBaseIds.Add(KnowledgeBaseId);
        form.ExposedFieldIds.Add(HrYearId);
        form.ExposedFieldIds.Add(FinanceYearId);
        return form;
    }

    [Fact] // AC-55, AC-58
    public void TwoExposedFieldsWithTheSameNameCollideUntilOneIsGivenAnAlias()
    {
        var form = ClusteredRagForm();

        Assert.Contains(form.Problems(Clustered), p => p.Contains("both be called \"year\"", StringComparison.Ordinal));

        form.SetAlias(HrYear, "year_hr");

        Assert.Empty(form.Problems(Clustered));
        var request = form.ToRequest(Clustered);
        Assert.Equal("year_hr", request.ExposedFields.Single(e => e.FieldId == HrYearId).Alias);
        Assert.Null(request.ExposedFields.Single(e => e.FieldId == FinanceYearId).Alias);
    }

    [Theory] // AC-55
    [InlineData("Year")]
    [InlineData("query")]
    [InlineData("price_min")]
    [InlineData("1st")]
    public void AnAliasThatIsNotAllowedIsFlaggedBeforeSaving(string alias)
    {
        var form = ClusteredRagForm();
        form.SetAlias(HrYear, alias);
        form.SetAlias(FinanceYear, "year_finance");

        Assert.Contains(form.Problems(Clustered), p => p.Contains("alias", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnAliasEqualToTheFieldNameIsNotSentAndAnEmptyOneTakesItBack()
    {
        var form = ClusteredRagForm();
        form.SetAlias(FinanceYear, "year");
        form.SetAlias(HrYear, "year_hr");

        Assert.Null(form.ToRequest(Clustered).ExposedFields.Single(e => e.FieldId == FinanceYearId).Alias);

        form.SetAlias(HrYear, "  ");

        Assert.Empty(form.Aliases);
    }

    [Fact]
    public void ChangingTheAliasOfAFieldMovesWhatWasTypedForItsFixedFilter()
    {
        var form = ClusteredRagForm();
        form.AddFixedFilter(FinanceYearId);
        form.SetAlias(HrYear, "year_hr");
        form.FilterValues["year_min"] = "2020";

        form.SetAlias(FinanceYear, "year_finance");

        Assert.Equal("2020", form.FilterValues["year_finance_min"]);
        Assert.False(form.FilterValues.ContainsKey("year_min"));
    }

    [Fact]
    public void ASavedAliasComesBackAndItsFixedFilterIsFoundUnderIt()
    {
        var detail = new RagToolDetail(
            Guid.NewGuid(),
            "company",
            "Searches.",
            [KnowledgeBaseId],
            [new ExposedFieldInfo(HrYearId, "year_hr"), new ExposedFieldInfo(FinanceYearId, "year")],
            Json("""{"year_hr":["2024"]}"""),
            5,
            null,
            6000,
            new ToolScope(ScopeKinds.Tenant, [], []),
            ToolStates.Enabled,
            ToolPolicies.Auto,
            null,
            []);

        var form = RagToolForm.From(detail, Clustered, Clustered);

        Assert.Equal("year_hr", form.Aliases[HrYearId]);
        Assert.False(form.Aliases.ContainsKey(FinanceYearId));
        Assert.Equal([HrYearId], form.FixedFilterFieldIds);
        Assert.Equal("2024", form.FilterValues["year_hr"]);
        Assert.Empty(form.UnshownFilters);
    }

    [Fact]
    public void TheParameterPreviewUsesTheAliases()
    {
        var aliases = new Dictionary<Guid, string> { [HrYearId] = "year_hr" };

        var names = RagToolView.ParameterNames([HrYear, FinanceYear], aliases, 2);

        Assert.Equal(["query", "knowledge_base", "year_min", "year_max", "year_hr"], names);
    }
}
