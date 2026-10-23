using System.Text.Json;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The logic behind the knowledge admin pages: draft mapping, registration, filters and the rag tool editor (spec 003, AC-49).</summary>
public sealed class KnowledgeAdminViewTests
{
    private static readonly Guid DepartmentId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b2001");
    private static readonly Guid YearId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b2002");
    private static readonly Guid TagsId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b2003");
    private static readonly Guid KnowledgeBaseId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b2011");
    private static readonly Guid OtherBaseId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b2012");

    private static readonly DictionaryField Department = new(DepartmentId, "department", DictionaryFieldTypes.Text, null, new ToolScope(ScopeKinds.Tenant, [], []), 0);
    private static readonly DictionaryField Year = new(YearId, "year", DictionaryFieldTypes.Number, null, new ToolScope(ScopeKinds.Tenant, [], []), 0);
    private static readonly DictionaryField Tags = new(TagsId, "tags", DictionaryFieldTypes.TextList, null, new ToolScope(ScopeKinds.Global, [], []), 0);
    private static readonly DictionaryField[] Dictionary = [Department, Year, Tags];

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DraftMapping FlatDraft() => new(
        new MappingRoles("chunk_text", "doc_id", "file_name", "page", "source_url"),
        [
            new DraftField("department", "department", DictionaryFieldTypes.Text, DepartmentId, []),
            new DraftField("meta.year", "year", DictionaryFieldTypes.Number, YearId, []),
            new DraftField("meta.reviewed", "reviewed", DictionaryFieldTypes.Boolean, null, []),
        ]);

    // --- Draft mapping (AC-7, AC-9, AC-10, AC-12) ---

    [Fact] // AC-7
    public void TheDraftFromTheServerBecomesRowsBoundToTheDictionary()
    {
        var draft = MappingDraft.From(FlatDraft());

        var reviews = draft.Review(Dictionary);

        Assert.Equal("chunk_text", draft.Text);
        Assert.Equal(["department", "year", "reviewed"], reviews.Select(r => r.Row.Name));
        Assert.Equal([false, false, true], reviews.Select(r => r.IsNew));
        Assert.All(reviews, r => Assert.Empty(r.Problems));
    }

    [Fact] // D-10
    public void ANewFieldNeedsTheAdminsConfirmationBeforeItIsCreated()
    {
        var draft = MappingDraft.From(FlatDraft());

        Assert.Equal(["reviewed"], draft.NewFields(Dictionary).Select(f => f.Name));
        Assert.Contains(draft.Problems(Dictionary), p => p.Contains("Confirm the creation", StringComparison.Ordinal));

        draft.ConfirmCreate = true;
        var (mapping, create) = draft.ToRequest(Dictionary);

        Assert.Empty(draft.Problems(Dictionary));
        Assert.Equal([new CreateFieldRequest("reviewed", DictionaryFieldTypes.Boolean)], create);
        Assert.Equal(
            [("department", DepartmentId, (string?)null), ("meta.year", YearId, null), ("meta.reviewed", null, "reviewed")],
            mapping.Fields.Select(f => (f.Path, f.DictionaryFieldId, f.Name)));
        Assert.Equal("chunk_text", mapping.Roles.Text);
    }

    [Fact]
    public void WithoutConfirmationNothingIsAskedToBeCreated()
    {
        var (_, create) = MappingDraft.From(FlatDraft()).ToRequest(Dictionary);

        Assert.Empty(create);
    }

    [Fact] // AC-12
    public void TheTextRoleIsRequired()
    {
        var draft = MappingDraft.From(new DraftMapping(new MappingRoles(null, null, null, null, null), []));

        Assert.Contains(draft.Problems(Dictionary), p => p.StartsWith("The text role is required", StringComparison.Ordinal));
    }

    [Fact] // AC-9
    public void TwoPathsWithTheSameNameAreFlaggedUntilOneIsRenamed()
    {
        var draft = MappingDraft.From(new DraftMapping(
            new MappingRoles("text", null, null, null, null),
            [
                new DraftField("a.topic", "topic", DictionaryFieldTypes.Text, null, []),
                new DraftField("b.topic", "topic", DictionaryFieldTypes.Text, null, []),
            ]));
        draft.ConfirmCreate = true;

        var reviews = draft.Review(Dictionary);
        Assert.All(reviews, r => Assert.Contains(r.Problems, p => p.Contains("also uses", StringComparison.Ordinal)));
        Assert.NotEmpty(draft.Problems(Dictionary));

        draft.Rows[1].Name = "b_topic";

        Assert.Empty(draft.Problems(Dictionary));
    }

    [Theory] // AC-2, AC-9
    [InlineData("Year")]
    [InlineData("my-field")]
    [InlineData("1st")]
    [InlineData("query")]
    [InlineData("knowledge_base")]
    [InlineData("year_min")]
    [InlineData("when_to")]
    public void AnInvalidOrReservedNameIsFlagged(string name)
    {
        var draft = MappingDraft.From(new DraftMapping(
            new MappingRoles("text", null, null, null, null),
            [new DraftField("x", name, DictionaryFieldTypes.Text, null, [])]));

        Assert.NotEmpty(draft.Review([])[0].Problems);
    }

    [Fact] // AC-10
    public void AnExistingFieldOfAnotherTypeBlocksUntilTheAdminAcceptsItsType()
    {
        var draft = MappingDraft.From(new DraftMapping(
            new MappingRoles("text", null, null, null, null),
            [new DraftField("year", "year", DictionaryFieldTypes.Text, YearId, ["knowledge_base.field_type_conflict"])]));

        var review = draft.Review(Dictionary).Single();
        Assert.True(review.HasTypeConflict);
        Assert.Contains("another cluster", Assert.Single(review.Problems), StringComparison.Ordinal);

        draft.Rows[0].AcceptExistingType = true;

        Assert.Empty(draft.Review(Dictionary).Single().Problems);
        Assert.Equal(YearId, draft.ToRequest(Dictionary).Mapping.Fields.Single().DictionaryFieldId);
    }

    [Fact]
    public void ARowTypedByHandReusesAnExistingFieldByName()
    {
        var draft = MappingDraft.From(new DraftMapping(new MappingRoles("text", null, null, null, null), []));
        var row = draft.AddRow();
        row.Path = "meta.dept";
        row.Name = "department";

        var review = draft.Review(Dictionary).Single();

        Assert.False(review.IsNew);
        Assert.False(review.HasTypeConflict);
        Assert.Empty(draft.Problems(Dictionary));
        draft.RemoveRow(row);
        Assert.Empty(draft.Rows);
    }

    [Fact]
    public void ARoleAndAFieldCannotShareAPath()
    {
        var draft = MappingDraft.From(new DraftMapping(
            new MappingRoles("text", null, "title", null, null),
            [new DraftField("title", "title", DictionaryFieldTypes.Text, null, [])]));
        draft.ConfirmCreate = true;

        Assert.Contains(draft.Problems(Dictionary), p => p.Contains("mapped twice", StringComparison.Ordinal));
    }

    [Fact]
    public void ASavedMappingComesBackAsRowsWithoutATypeOpinion()
    {
        var draft = MappingDraft.From(new MappingInfo(
            new MappingRoles("text", null, "title", null, null),
            [new MappedFieldInfo("department", DepartmentId, "department")]));

        Assert.Empty(draft.Problems(Dictionary));
        Assert.Null(draft.Rows[0].Type);
        Assert.Equal("title", draft.Title);
    }

    // --- Registration (AC-11, AC-13) ---

    private static KnowledgeBaseDraft ConnectedDraft() => new()
    {
        Name = "policies",
        Description = "Company policies.",
        Url = " http://qdrant:6334 ",
        Collection = "docs",
        EmbeddingModel = "nomic-embed-text",
        QueryPrefix = "search_query: ",
    };

    [Fact]
    public void TheConnectionStepNeedsTheSameThingsTheServerDoes()
    {
        var empty = new KnowledgeBaseDraft();

        Assert.Equal(5, empty.ConnectionProblems().Count);
        Assert.Empty(ConnectedDraft().ConnectionProblems());
    }

    [Theory]
    [InlineData("Policies")]
    [InlineData("-policies")]
    [InlineData("has space")]
    public void ANameThatIsNotAllowedIsRefused(string name)
    {
        var draft = ConnectedDraft();
        draft.Name = name;

        Assert.Contains(draft.ConnectionProblems(), p => p.StartsWith("The name is", StringComparison.Ordinal));
    }

    [Fact]
    public void TheInspectRequestKeepsThePrefixAsTypedAndOmitsWhatIsEmpty()
    {
        var draft = ConnectedDraft();

        var request = draft.ToInspectRequest();

        Assert.Equal(("http://qdrant:6334", "docs", (string?)null, (string?)null), (request.Connection.Url, request.Connection.Collection, request.Connection.ApiKey, request.Schema));
        Assert.Equal("search_query: ", request.QueryPrefix);
        Assert.Equal(KnowledgeBaseKinds.Qdrant, request.Kind);

        draft.Schema = """{"type":"object"}""";
        Assert.Equal("""{"type":"object"}""", draft.ToInspectRequest().Schema);
    }

    [Fact]
    public void ASchemaOverTheLimitIsRefusedBeforeItIsSent()
    {
        var draft = ConnectedDraft();
        draft.Schema = new string('x', KnowledgeBaseDraft.MaxSchemaBytes + 1);

        Assert.NotNull(draft.SchemaProblem());
    }

    [Fact] // AC-11
    public void SeveralVectorsAndNoneChosenAsksTheAdminToChoose()
    {
        var draft = ConnectedDraft();
        var inspection = Inspection(vectors: [new VectorInfo("dense", 768, "Cosine"), new VectorInfo("other", 128, "Dot")]);

        Assert.True(draft.NeedsVectorChoice(inspection));

        draft.VectorName = "dense";
        Assert.False(draft.NeedsVectorChoice(inspection));
    }

    [Fact]
    public void ASingleNamedVectorIsChosenForTheAdmin()
    {
        var draft = ConnectedDraft();

        draft.UseInspection(Inspection(vectors: [new VectorInfo("dense", 768, "Cosine")]));

        Assert.Equal("dense", draft.VectorName);
        Assert.NotNull(draft.Mapping);
    }

    [Fact]
    public void AnInspectionIsSummarisedInPlainStatements()
    {
        var findings = KnowledgeAdminView.Findings(Inspection(vectors: [new VectorInfo(null, 768, "Cosine")], dimensionMatch: false, embeddingSize: 384));

        Assert.Contains("The collection exists.", findings);
        Assert.Contains("Vectors (size · distance): 768 · Cosine.", findings);
        Assert.Contains(findings, f => f.Contains("do not match", StringComparison.Ordinal));
    }

    [Fact]
    public void ACollectionThatCannotBeReadCannotBeReviewed()
    {
        Assert.True(KnowledgeAdminView.CanReview(Inspection()));
        Assert.False(KnowledgeAdminView.CanReview(Inspection(reachable: false)));
        Assert.False(KnowledgeAdminView.CanReview(Inspection(collectionExists: false)));
    }

    [Theory]
    [InlineData("knowledge_base.qdrant_unreachable", WarningLevel.Error)]
    [InlineData("knowledge_base.dimension_mismatch", WarningLevel.Error)]
    [InlineData("knowledge_base.mixed_types", WarningLevel.Warning)]
    [InlineData("knowledge_base.field_unindexed", WarningLevel.Info)]
    public void WarningsAreShownAtTheLevelTheirCodeStandsFor(string code, WarningLevel level)
    {
        Assert.Equal(level, KnowledgeAdminView.LevelOf(new KnowledgeBaseWarning(code, "m", null)));
    }

    [Fact]
    public void TheFullDraftTurnsIntoTheRequestTheServerTakes()
    {
        var draft = ConnectedDraft();
        draft.ApiKey = "s3cret";
        draft.UseInspection(Inspection());
        draft.Mapping!.ConfirmCreate = true;
        draft.Scope.Kind = ScopeKinds.Global;

        Assert.Empty(draft.Problems(Dictionary));
        var request = draft.ToRequest(Dictionary);

        Assert.Equal(("policies", KnowledgeBaseKinds.Qdrant, "http://qdrant:6334", "s3cret"), (request.Name, request.Kind, request.Connection.Url, request.Connection.ApiKey));
        Assert.Equal(ScopeKinds.Global, request.Scope.Kind);
        Assert.Contains(draft.Summary(Dictionary, []), line => line.Contains("every tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARegistrationWithoutAReviewedMappingCannotBeSaved()
    {
        Assert.Contains(ConnectedDraft().Problems(Dictionary), p => p.StartsWith("Review the mapping", StringComparison.Ordinal));
    }

    [Fact]
    public void ADraftReadFromADetailEditsInPlaceAndKnowsWhenTheKeyMustBeTypedAgain()
    {
        var detail = new KnowledgeBaseDetail(
            KnowledgeBaseId,
            "policies",
            "Company policies.",
            KnowledgeBaseStatuses.Available,
            false,
            KnowledgeBaseKinds.Qdrant,
            new ConnectionInfo("http://qdrant:6334", "docs", "dense", HasApiKey: true),
            "nomic-embed-text",
            null,
            new MappingInfo(new MappingRoles("text", null, null, null, null), [new MappedFieldInfo("department", DepartmentId, "department")]),
            new ToolScope(ScopeKinds.Tenant, [], []),
            null,
            null);

        var draft = KnowledgeBaseDraft.From(detail);

        Assert.False(draft.IsNew);
        Assert.Empty(draft.Problems(Dictionary));
        Assert.False(draft.NeedsApiKeyAgain);
        Assert.Null(draft.ToRequest(Dictionary).Connection.ApiKey);

        draft.Url = "http://elsewhere:6334";

        Assert.True(draft.NeedsApiKeyAgain);
    }

    [Fact]
    public void AKnowledgeBaseOfAnotherTenantCannotBeEdited()
    {
        var shared = new KnowledgeBaseDetail(KnowledgeBaseId, "shared", "Shared.", KnowledgeBaseStatuses.Available, true, null, null, null, null, null, null, null, null);

        Assert.Throws<ArgumentException>(() => KnowledgeBaseDraft.From(shared));
    }

    // --- Scope ---

    [Fact]
    public void TheScopeFormBuildsTheScopeTheApiTakes()
    {
        var assistant = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        Assert.Equal(ScopeKinds.Tenant, new ScopeForm().Build().Scope!.Kind);
        Assert.Equal([tenant], new ScopeForm { TenantsText = $"{tenant}, {tenant}" }.Build().Scope!.TenantIds);
        Assert.Equal([assistant], new ScopeForm { Kind = ScopeKinds.Assistant, AssistantIds = [assistant] }.Build().Scope!.AssistantIds);
        Assert.Empty(new ScopeForm { Kind = ScopeKinds.Global, TenantsText = "ignored" }.Build().Scope!.TenantIds);
    }

    [Fact]
    public void AScopeWithoutAssistantsOrWithABadIdIsRefused()
    {
        Assert.NotNull(new ScopeForm { Kind = ScopeKinds.Assistant }.Build().Error);
        Assert.NotNull(new ScopeForm { TenantsText = "not-an-id" }.Build().Error);
    }

    // --- Filters (AC-25, D-11) ---

    [Fact]
    public void EachTypeGivesTheParametersOfTheGeneratedSchema()
    {
        string[] Names(string type) => [.. FilterInputs.ParametersOf("f", type).Select(p => p.Name)];

        Assert.Equal(["f"], Names(DictionaryFieldTypes.Text));
        Assert.Equal(["f_min", "f_max"], Names(DictionaryFieldTypes.Number));
        Assert.Equal(["f_from", "f_to"], Names(DictionaryFieldTypes.Date));
        Assert.Equal(["f"], Names(DictionaryFieldTypes.Boolean));
        Assert.Equal(["f"], Names(DictionaryFieldTypes.TextList));
    }

    [Fact]
    public void TypedFiltersAreReadIntoTheShapesOfTheSchema()
    {
        var parameters = FilterInputs.ParametersOf("department", DictionaryFieldTypes.Text)
            .Concat(FilterInputs.ParametersOf("year", DictionaryFieldTypes.Number))
            .Concat(FilterInputs.ParametersOf("published_on", DictionaryFieldTypes.Date))
            .Concat(FilterInputs.ParametersOf("confidential", DictionaryFieldTypes.Boolean))
            .ToList();
        var inputs = new Dictionary<string, string>
        {
            ["department"] = "hr, legal\nfinance",
            ["year_min"] = "2023",
            ["year_max"] = " ",
            ["published_on_from"] = "2024-01-31",
            ["confidential"] = "false",
        };

        var (filters, error) = FilterInputs.Read(inputs, parameters);

        Assert.Null(error);
        Assert.Equal(["hr", "legal", "finance"], (string[])filters!["department"]);
        Assert.Equal(2023d, filters["year_min"]);
        Assert.Equal("2024-01-31", filters["published_on_from"]);
        Assert.Equal(false, filters["confidential"]);
        Assert.Equal(4, filters.Count);
    }

    [Theory] // AC-31
    [InlineData("year_min", "abc")]
    [InlineData("year_min", "NaN")]
    [InlineData("published_on_from", "yesterday")]
    [InlineData("published_on_from", "31/01/2024")]
    [InlineData("confidential", "maybe")]
    public void AValueThatDoesNotFitItsTypeIsRefusedBeforeAnythingIsSent(string parameter, string value)
    {
        var parameters = FilterInputs.ParametersOf("year", DictionaryFieldTypes.Number)
            .Concat(FilterInputs.ParametersOf("published_on", DictionaryFieldTypes.Date))
            .Concat(FilterInputs.ParametersOf("confidential", DictionaryFieldTypes.Boolean));

        var (filters, error) = FilterInputs.Read(new Dictionary<string, string> { [parameter] = value }, parameters);

        Assert.Null(filters);
        Assert.NotNull(error);
    }

    [Fact]
    public void ATextFilterHasAtMostTwentyValues()
    {
        var parameters = FilterInputs.ParametersOf("tags", DictionaryFieldTypes.TextList);

        var (filters, error) = FilterInputs.Read(new Dictionary<string, string> { ["tags"] = string.Join(',', Enumerable.Range(0, 21)) }, parameters);

        Assert.Null(filters);
        Assert.NotNull(error);
    }

    [Fact]
    public void StoredFiltersShowAgainAsTheInputsTheyCameFrom()
    {
        var inputs = FilterInputs.Show(Json("""{"department":["hr","legal"],"year_min":2024,"confidential":true}"""));

        Assert.Equal("hr, legal", inputs["department"]);
        Assert.Equal("2024", inputs["year_min"]);
        Assert.Equal("true", inputs["confidential"]);
        Assert.Empty(FilterInputs.Show(Json("[]")));
    }

    // --- Rag tool editor (AC-19 to AC-23) ---

    private static KnowledgeBaseDetail Detail(Guid id, params Guid[] fieldIds) => new(
        id,
        "kb",
        "A knowledge base.",
        KnowledgeBaseStatuses.Available,
        false,
        KnowledgeBaseKinds.Qdrant,
        null,
        null,
        null,
        new MappingInfo(new MappingRoles("text", null, null, null, null), [.. fieldIds.Select(f => new MappedFieldInfo(f.ToString(), f, null))]),
        null,
        null,
        null);

    [Fact]
    public void TheEditorOffersTheFieldsTheSelectedKnowledgeBasesMap()
    {
        var details = new Dictionary<Guid, KnowledgeBaseDetail>
        {
            [KnowledgeBaseId] = Detail(KnowledgeBaseId, DepartmentId),
            [OtherBaseId] = Detail(OtherBaseId, YearId),
        };

        var one = RagToolView.Candidates([KnowledgeBaseId], details, Dictionary);
        var both = RagToolView.Candidates([KnowledgeBaseId, OtherBaseId], details, Dictionary);

        Assert.Equal(["department"], one.Fields.Select(f => f.Name));
        Assert.Equal(["department", "year"], both.Fields.Select(f => f.Name));
        Assert.True(both.Complete);
    }

    [Fact]
    public void ASharedKnowledgeBaseHidesItsMappingSoEveryFieldIsOffered()
    {
        var choices = RagToolView.Candidates([KnowledgeBaseId], new Dictionary<Guid, KnowledgeBaseDetail>(), Dictionary);

        Assert.False(choices.Complete);
        Assert.Equal(3, choices.Fields.Count);
    }

    [Fact]
    public void SelectingKnowledgeBasesExposesTheirFieldsUpToTheLimitAndDeselectingRemovesThem()
    {
        var many = Enumerable.Range(0, 12).Select(i => new DictionaryField(Guid.NewGuid(), $"f{i:00}", DictionaryFieldTypes.Text, null, new ToolScope(ScopeKinds.Tenant, [], []), 0)).ToList();
        var form = new RagToolForm();

        form.ApplyCandidates(many, exposeNew: true);

        Assert.Equal(RagToolLimits.MaxExposedFields, form.ExposedFieldIds.Count);

        form.ApplyCandidates([many[0]], exposeNew: true);

        Assert.Equal([many[0].Id], form.ExposedFieldIds);
    }

    [Fact]
    public void AFieldTheAdminTookOffStaysOffWhenAnotherKnowledgeBaseIsAdded()
    {
        var form = new RagToolForm();
        form.ApplyCandidates([Department, Year], exposeNew: true);
        form.ExposedFieldIds.Remove(YearId);

        form.ApplyCandidates([Department, Year, Tags], exposeNew: true);

        Assert.Equal([DepartmentId, TagsId], form.ExposedFieldIds.Order());
    }

    [Fact]
    public void TheParameterPreviewFollowsTheRulesOfTheGeneratedSchema()
    {
        Assert.Equal(["query", "department", "year_min", "year_max"], RagToolView.ParameterNames([Year, Department], 1));
        Assert.Contains("knowledge_base", RagToolView.ParameterNames([], 2));
        Assert.DoesNotContain("knowledge_base", RagToolView.ParameterNames([], 1));
    }

    [Fact]
    public void ARagToolWithEverythingFilledIsValidAndSendsTheLimitsAndFixedFilters()
    {
        var form = ValidRagForm();
        form.AddFixedFilter(YearId);
        form.FilterValues["year_min"] = "2024";
        form.ScoreThreshold = "0.4";

        Assert.Empty(form.Problems(Dictionary));
        var request = form.ToRequest(Dictionary);

        Assert.Equal(("company", ToolPolicies.Auto, 5, 0.4, 6000), (request.Name, request.Policy, request.TopK, request.ScoreThreshold, request.MaxResultCharacters));
        Assert.Equal([KnowledgeBaseId], request.KnowledgeBaseIds);
        Assert.Equal(2024d, request.FixedFilters!["year_min"]);
    }

    [Fact]
    public void ARagToolWithoutFixedFiltersSendsNone()
    {
        Assert.Null(ValidRagForm().ToRequest(Dictionary).FixedFilters);
    }

    [Fact] // AC-21
    public void TheLimitsOfARagToolAreCheckedBeforeSaving()
    {
        var form = ValidRagForm();
        form.TopK = 21;
        form.MaxResultCharacters = 100;
        form.ScoreThreshold = "high";
        form.Name = "Has Space";
        form.Description = " ";
        form.KnowledgeBaseIds.Clear();

        Assert.Equal(6, form.Problems(Dictionary).Count);
    }

    [Fact] // AC-21
    public void MoreThanTenKnowledgeBasesOrFieldsIsRefused()
    {
        var form = ValidRagForm();
        form.KnowledgeBaseIds.AddRange(Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()));
        for (var i = 0; i < 11; i++)
        {
            form.ExposedFieldIds.Add(Guid.NewGuid());
        }

        var problems = form.Problems(Dictionary);

        Assert.Contains(problems, p => p.Contains("up to 10 knowledge bases", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("up to 10 fields", StringComparison.Ordinal));
    }

    [Fact]
    public void AFixedFilterWithoutAValueIsRefusedBecauseItWouldRestrictNothing()
    {
        var form = ValidRagForm();
        form.AddFixedFilter(DepartmentId);

        Assert.Contains(form.Problems(Dictionary), p => p.Contains("has no value", StringComparison.Ordinal));

        form.FilterValues["department"] = "hr";
        Assert.Empty(form.Problems(Dictionary));

        form.RemoveFixedFilter(DepartmentId, Dictionary);
        Assert.Empty(form.FilterValues);
        Assert.Empty(form.FixedFilterFieldIds);
    }

    [Fact]
    public void ASavedRagToolIsReadBackWithItsFixedFiltersAndTheOnesItCannotShow()
    {
        var detail = new RagToolDetail(
            Guid.NewGuid(),
            "company",
            "Searches.",
            [KnowledgeBaseId],
            [new ExposedFieldInfo(DepartmentId, "department")],
            Json("""{"department":["hr"],"gone_min":3}"""),
            7,
            0.25,
            4000,
            new ToolScope(ScopeKinds.Assistant, [], [Guid.NewGuid()]),
            ToolStates.Enabled,
            ToolPolicies.Confirm,
            null,
            []);

        var form = RagToolForm.From(detail, Dictionary);

        Assert.Equal([DepartmentId], form.FixedFilterFieldIds);
        Assert.Equal("hr", form.FilterValues["department"]);
        Assert.Equal(["gone_min"], form.UnshownFilters);
        Assert.Equal(("0.25", 7, 4000, ToolPolicies.Confirm), (form.ScoreThreshold, form.TopK, form.MaxResultCharacters, form.Policy));
        Assert.Equal(ScopeKinds.Assistant, form.Scope.Kind);
        Assert.False(form.IsNew);
    }

    [Theory]
    [InlineData(850, "about 850 tokens of the context")]
    [InlineData(1200, "about 1.2k tokens of the context")]
    public void TheSizeOfADefinitionIsSaidInTokens(int tokens, string expected)
    {
        Assert.Equal(expected, RagToolView.SizeText(tokens));
    }

    // --- Search results (AC-40) ---

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("http://example.com/a", "http://example.com/a")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ftp://example.com/a", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void OnlyHttpAndHttpsAreLinks(string? url, string? expected)
    {
        Assert.Equal(expected, KnowledgeAdminView.SafeUrl(url));
    }

    [Fact]
    public void ASourceIsDescribedByTitleAndPositionWithTheDocumentIdAsAFallback()
    {
        Assert.Equal("Leave policy (p. 3)", KnowledgeAdminView.DescribeSource(new TestSearchSource("kb", "d1", "Leave policy", "p. 3", null)));
        Assert.Equal("d1", KnowledgeAdminView.DescribeSource(new TestSearchSource("kb", "d1", null, null, null)));
        Assert.Equal("Untitled", KnowledgeAdminView.DescribeSource(new TestSearchSource("kb", null, null, null, null)));
    }

    private static RagToolForm ValidRagForm()
    {
        var form = new RagToolForm { Name = "company", Description = "Searches the company policies." };
        form.KnowledgeBaseIds.Add(KnowledgeBaseId);
        form.ExposedFieldIds.Add(DepartmentId);
        return form;
    }

    private static InspectResult Inspection(
        IReadOnlyList<VectorInfo>? vectors = null,
        bool reachable = true,
        bool collectionExists = true,
        bool? dimensionMatch = true,
        int? embeddingSize = 768) => new(
            reachable,
            collectionExists,
            vectors ?? [new VectorInfo(null, 768, "Cosine")],
            embeddingSize,
            dimensionMatch,
            new DraftMapping(new MappingRoles("text", null, null, null, null), [new DraftField("department", "department", DictionaryFieldTypes.Text, DepartmentId, [])]),
            [],
            [],
            []);
}
