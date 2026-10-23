using System.Text.Json;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The lookup tool form and the parts it shares with the rag tool form, without any markup (spec 007 AC-43).</summary>
public sealed class LookupToolFormTests
{
    private static readonly Guid DepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b7001");
    private static readonly Guid YearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b7002");
    private static readonly Guid BaseId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b7011");
    private static readonly Guid OtherBaseId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b7012");

    private static readonly IReadOnlyList<DictionaryField> Dictionary =
    [
        new(DepartmentId, "department", DictionaryFieldTypes.Text, null, new ToolScope(ScopeKinds.Tenant, [], []), 0),
        new(YearId, "year", DictionaryFieldTypes.Number, null, new ToolScope(ScopeKinds.Tenant, [], []), 0),
    ];

    private static LookupChoice Choice(string name, Guid? knowledgeBase = null) =>
        new(Guid.NewGuid(), name, knowledgeBase is null ? LookupKinds.Manual : LookupKinds.Computed, knowledgeBase, knowledgeBase is null ? null : "contracts");

    private static LookupToolForm Valid(params LookupChoice[] choices)
    {
        var form = new LookupToolForm { Name = "support", Description = "Reads the lists." };
        foreach (var choice in choices)
        {
            form.AddLookup(choice.Id);
        }

        return form;
    }

    [Fact]
    public void ALookupToolNeedsANameADescriptionAndALookup()
    {
        var problems = new LookupToolForm().Problems(Dictionary, []);

        Assert.Contains(problems, p => p.Contains("The name is 1 to 40 characters", StringComparison.Ordinal) && p.Contains("lookup__name", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("The description is required", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Choose at least one lookup", StringComparison.Ordinal));
    }

    [Fact]
    public void ALookupCanBeBoundOnlyOnceAndAtMostTenAreAllowed()
    {
        var choices = Enumerable.Range(0, 11).Select(i => Choice($"list{i}")).ToArray();
        var form = Valid(choices[0]);
        form.AddLookup(choices[0].Id);

        Assert.Single(form.Lookups);

        foreach (var choice in choices)
        {
            form.AddLookup(choice.Id);
        }

        Assert.Contains(form.Problems(Dictionary, choices), p => p.Contains("up to 10 lookups", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoListsOfOneNameNeedAnAliasButADistinctAliasMakesThemFine()
    {
        var first = Choice("supplier", BaseId);
        var second = Choice("supplier", OtherBaseId);
        var form = Valid(first, second);

        Assert.Contains(form.Problems(Dictionary, [first, second]), p => p.Contains("Two lookups would be called \"supplier\"", StringComparison.Ordinal));

        form.Lookups[1].Alias = "old_supplier";

        Assert.Empty(form.Problems(Dictionary, [first, second]));
    }

    [Fact]
    public void AnAliasMustBeLowerCaseLettersDigitsAndUnderscores()
    {
        var choice = Choice("supplier", BaseId);
        var form = Valid(choice);
        form.Lookups[0].Alias = "Old Supplier";

        Assert.Contains(form.Problems(Dictionary, [choice]), p => p.Contains("alias \"Old Supplier\" of a lookup is not allowed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("lookup")]
    [InlineData("knowledge_base")]
    [InlineData("search")]
    [InlineData("sort")]
    [InlineData("limit")]
    [InlineData("cursor")]
    public void AFieldAliasCannotTakeAParameterOfTheToolItself(string alias)
    {
        var choice = Choice("supplier", BaseId);
        var form = Valid(choice);
        form.ExposedFieldIds.Add(DepartmentId);
        form.SetAlias(Dictionary[0], alias);

        Assert.Contains(form.Problems(Dictionary, [choice]), p => p.Contains($"alias \"{alias}\" is reserved", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRequestCarriesTheLookupsTheKnowledgeBasesTheFieldsAndTheFilters()
    {
        var supplier = Choice("supplier", BaseId);
        var nationality = Choice("nationality");
        var form = Valid(supplier, nationality);
        form.Lookups[0].Alias = " vendor ";
        form.KnowledgeBaseIds.Add(BaseId);
        form.ExposedFieldIds.Add(DepartmentId);
        form.AddFixedFilter(YearId);
        form.FilterValues["year_min"] = "2024";

        Assert.Empty(form.Problems(Dictionary, [supplier, nationality]));
        var request = form.ToRequest(Dictionary);

        Assert.Equal(["vendor", null], request.Lookups.Select(l => l.Alias));
        Assert.Equal([BaseId], request.KnowledgeBaseIds);
        Assert.Equal([DepartmentId], request.ExposedFields.Select(f => f.FieldId));
        Assert.Equal(2024d, Assert.IsType<double>(request.FixedFilters!["year_min"]));
        Assert.Equal(ToolPolicies.Auto, request.Policy);
    }

    [Fact]
    public void ASavedToolIsLoadedWithItsAliasesAndFilters()
    {
        var lookupId = Guid.NewGuid();
        var detail = new LookupToolDetail(
            Guid.NewGuid(),
            "contracts",
            "Reads.",
            [new BoundLookup(lookupId, "vendor")],
            [BaseId],
            [new ExposedFieldInfo(DepartmentId, "dept")],
            JsonDocument.Parse("""{"year_min":2024}""").RootElement.Clone(),
            new ToolScope(ScopeKinds.Tenant, [], []),
            "Enabled",
            ToolPolicies.Confirm,
            null,
            []);

        var form = LookupToolForm.From(detail, Dictionary);

        Assert.Equal(("contracts", ToolPolicies.Confirm), (form.OriginalName, form.Policy));
        Assert.Equal("vendor", Assert.Single(form.Lookups).Alias);
        Assert.Equal("dept", form.Aliases[DepartmentId]);
        Assert.Equal([YearId], form.FixedFilterFieldIds);
        Assert.Equal("2024", form.FilterValues["year_min"]);
    }

    [Fact]
    public void TheKnowledgeBasesInUseAreTheOnesTheToolAsksAndTheOnesItsListsComeFrom()
    {
        var supplier = Choice("supplier", BaseId);
        var nationality = Choice("nationality");
        var form = Valid(supplier, nationality);
        form.KnowledgeBaseIds.Add(OtherBaseId);

        Assert.Equal([OtherBaseId, BaseId], LookupToolView.KnowledgeBasesInUse(form, [supplier, nationality]));
    }

    [Fact]
    public void TheKnowledgeBaseOfAListOfEntitiesIsNotOneWhoseFieldsAreOffered()
    {
        var computed = Choice("supplier", BaseId);
        var entities = new LookupChoice(Guid.NewGuid(), "vendors", LookupKinds.Entity, OtherBaseId, "vendors");
        var form = Valid(computed, entities);

        Assert.Equal([BaseId], LookupToolView.KnowledgeBasesInUse(form, [computed, entities]));
    }

    [Fact]
    public void TheChoicesNameEachListByWhereItComesFromAndLeaveOutSharedKnowledgeBases()
    {
        var own = new KnowledgeBaseListItem(BaseId, "contracts", "d", "Available", false, "Qdrant", new ToolScope("Tenant", [], []), null, null);
        var shared = new KnowledgeBaseListItem(OtherBaseId, "handbook", "d", "Available", true, null, null, null, null);
        var lookups = new Dictionary<Guid, IReadOnlyList<KnowledgeBaseLookupInfo>>
        {
            [BaseId] = [new(Guid.NewGuid(), "supplier", LookupKinds.Computed, "supplier", "documents", []), new(Guid.NewGuid(), "contracts", LookupKinds.Entity, null, null, [])],
            [OtherBaseId] = [new(Guid.NewGuid(), "secret", LookupKinds.Computed, "secret", "documents", [])],
        };
        var manual = new[] { new ManualLookupInfo(Guid.NewGuid(), "nationality", null, new ToolScope(ScopeKinds.Global, [], []), 3, true) };

        var choices = LookupToolView.Choices([own, shared], lookups, manual);

        Assert.Equal(["supplier · contracts", "contracts (entities)", "nationality (typed by hand, shared with you)"], choices.Select(c => c.Label));
    }

    // --- The rag tool form after sharing its base ---

    [Fact]
    public void TheRagFormKeepsItsRulesAndReservesDocumentIds()
    {
        var form = new RagToolForm { Name = "support", Description = "Searches." };
        form.KnowledgeBaseIds.Add(BaseId);
        form.ExposedFieldIds.Add(DepartmentId);
        form.SetAlias(Dictionary[0], "document_ids");

        Assert.Contains(form.Problems(Dictionary), p => p.Contains("alias \"document_ids\" is reserved", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDocumentIdsSwitchFollowsTheKnowledgeBasesUntilTheAdminChooses()
    {
        var form = new RagToolForm();

        form.ApplyDocumentIds(supported: true);
        Assert.True(form.AllowDocumentIds);

        form.ApplyDocumentIds(supported: false);
        Assert.False(form.AllowDocumentIds);

        form.ChooseDocumentIds(allow: false);
        form.ApplyDocumentIds(supported: true);
        Assert.False(form.AllowDocumentIds);
    }

    [Fact]
    public void ASavedToolKeepsItsDocumentIdsValueWhateverTheKnowledgeBasesSay()
    {
        var detail = new RagToolDetail(
            Guid.NewGuid(), "t", "d", [BaseId], [], JsonDocument.Parse("{}").RootElement.Clone(), 5, null, 6000,
            new ToolScope(ScopeKinds.Tenant, [], []), "Enabled", ToolPolicies.Auto, null, [], AllowDocumentIds: false);
        var form = RagToolForm.From(detail, Dictionary);

        form.ApplyDocumentIds(supported: true);

        Assert.False(form.AllowDocumentIds);
        Assert.False(form.ToRequest(Dictionary).AllowDocumentIds);
    }

    [Fact]
    public void WhetherTheKnowledgeBasesMapTheDocumentIdIsReadFromTheirDetails()
    {
        KnowledgeBaseDetail Detail(Guid id, string? documentId) => new(
            id, "kb", "d", "Available", false, "Qdrant", null, null, null,
            new MappingInfo(new MappingRoles("text", documentId, null, null, null), []), null, null, null);
        var details = new Dictionary<Guid, KnowledgeBaseDetail> { [BaseId] = Detail(BaseId, null), [OtherBaseId] = Detail(OtherBaseId, "doc_id") };
        var shared = Guid.NewGuid();

        Assert.False(RagToolView.SupportsDocumentIds([BaseId], details));
        Assert.True(RagToolView.SupportsDocumentIds([BaseId, OtherBaseId], details));
        Assert.Null(RagToolView.SupportsDocumentIds([BaseId, shared], details));
        Assert.True(RagToolView.SupportsDocumentIds([shared, OtherBaseId], details));
    }
}
