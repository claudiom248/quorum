using System.Text.Json.Nodes;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.Rag;
using Quorum.SampleKnowledgeBase;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>
/// The typed client and the view classes of the knowledge admin pages against the real server and a real Qdrant: the whole
/// registration flow and the rag tool editor, with no mock of the API (spec 003, AC-49).
/// </summary>
public sealed class KnowledgeAdminClientTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAdminManagesTheDictionaryThroughTheClient()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var api = new QuorumApi(setup.Admin);

        var created = await api.CreateDictionaryFieldAsync(new DictionaryFieldRequest("region", DictionaryFieldTypes.Text, "Sales region", ScopeKinds.Tenant), Ct);
        var duplicate = await api.CreateDictionaryFieldAsync(new DictionaryFieldRequest("region", DictionaryFieldTypes.Text, null, ScopeKinds.Tenant), Ct);
        var reserved = await api.CreateDictionaryFieldAsync(new DictionaryFieldRequest("year_min", DictionaryFieldTypes.Number, null, ScopeKinds.Tenant), Ct);

        Assert.True(created.IsSuccess);
        Assert.Equal("knowledge_base.field_name_taken", duplicate.Problem!.Code);
        Assert.Equal("knowledge_base.reserved_field_name", reserved.Problem!.Code);

        Assert.True((await api.UpdateDictionaryFieldAsync(created.Value.Id, "Where it was sold", Ct)).IsSuccess);
        var listed = (await api.ListDictionaryAsync(Ct)).Value.Single(f => f.Id == created.Value.Id);
        Assert.Equal(("region", DictionaryFieldTypes.Text, "Where it was sold", ScopeKinds.Tenant, 0), (listed.Name, listed.Type, listed.Description, listed.Scope.Kind, listed.UsedByCount));

        Assert.True((await api.DeleteDictionaryFieldAsync(created.Value.Id, Ct)).IsSuccess);
        Assert.Empty((await api.ListDictionaryAsync(Ct)).Value);
    }

    [Fact]
    public async Task AnAdminRegistersVerifiesSearchesEditsAndDeletesAKnowledgeBaseThroughTheClient()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var api = new QuorumApi(setup.Admin);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);

        // Connection → inspection: the draft the guided registration starts from.
        var draft = new KnowledgeBaseDraft
        {
            Name = "policies",
            Description = "Company policies.",
            Url = setup.Url,
            Collection = collection,
            EmbeddingModel = KnowledgeBaseApi.EmbeddingModel,
        };
        Assert.Empty(draft.ConnectionProblems());
        var inspection = (await api.InspectKnowledgeBaseAsync(draft.ToInspectRequest(), Ct)).Value;
        Assert.True(KnowledgeAdminView.CanReview(inspection));
        Assert.Equal(true, inspection.DimensionMatch);
        draft.UseInspection(inspection);

        // Mapping review: the fields are new, so the admin has to confirm their creation.
        var dictionary = (await api.ListDictionaryAsync(Ct)).Value;
        Assert.Equal("text", draft.Mapping!.Text);
        Assert.Contains(draft.Mapping.Problems(dictionary), p => p.Contains("Confirm the creation", StringComparison.Ordinal));
        draft.Mapping.ConfirmCreate = true;
        Assert.Empty(draft.Problems(dictionary));

        // Save: registered, available, the dictionary grew with the confirmed fields.
        var registered = (await api.CreateKnowledgeBaseAsync(draft.ToRequest(dictionary), Ct)).Value;
        Assert.Equal(KnowledgeBaseStatuses.Available, registered.Status);
        Assert.Empty(registered.Warnings);
        dictionary = (await api.ListDictionaryAsync(Ct)).Value;
        Assert.Contains("department", dictionary.Select(f => f.Name));

        // The list and the detail: the key is never returned and the mapping comes back with the names of the fields.
        var listed = Assert.Single((await api.ListKnowledgeBasesAsync(Ct)).Value);
        Assert.Equal(("policies", KnowledgeBaseStatuses.Available, false), (listed.Name, listed.Status, listed.Shared));
        var detail = (await api.GetKnowledgeBaseAsync(registered.Id, Ct)).Value;
        Assert.False(detail.Connection!.HasApiKey);
        Assert.Contains("department", detail.Mapping!.Fields.Select(f => f.Name));

        // Test search with a filter built the way the page builds it.
        var filters = FilterInputs.Read(new Dictionary<string, string> { ["department"] = "hr" }, FilterInputs.ParametersOf("department", DictionaryFieldTypes.Text));
        var found = (await api.SearchKnowledgeBaseAsync(registered.Id, new TestSearchRequest("vacation", filters.Filters, 5), Ct)).Value;
        Assert.NotEmpty(found.Results);
        Assert.All(found.Results, hit => Assert.False(string.IsNullOrWhiteSpace(hit.Text)));

        // Verify.
        Assert.Equal(KnowledgeBaseStatuses.Available, (await api.VerifyKnowledgeBaseAsync(registered.Id, Ct)).Value.Status);

        // Edit: the draft read back from the detail has no problems as it stands, and saves.
        var edit = KnowledgeBaseDraft.From(detail);
        edit.Description = "Company policies, all departments.";
        Assert.Empty(edit.Problems(dictionary));
        Assert.True((await api.UpdateKnowledgeBaseAsync(registered.Id, edit.ToRequest(dictionary), Ct)).IsSuccess);
        Assert.Equal(edit.Description, (await api.GetKnowledgeBaseAsync(registered.Id, Ct)).Value.Description);

        // A second registration with the same name is refused with the server's code.
        var again = await api.CreateKnowledgeBaseAsync(draft.ToRequest(dictionary), Ct);
        Assert.Equal("knowledge_base.name_taken", again.Problem!.Code);

        Assert.True((await api.DeleteKnowledgeBaseAsync(registered.Id, Ct)).IsSuccess);
        Assert.Empty((await api.ListKnowledgeBasesAsync(Ct)).Value);
    }

    [Fact]
    public async Task AMissingCollectionIsReportedByTheInspectionAsAnError()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var api = new QuorumApi(setup.Admin);
        var draft = new KnowledgeBaseDraft
        {
            Name = "later",
            Description = "Not ready yet.",
            Url = setup.Url,
            Collection = "does_not_exist",
            EmbeddingModel = KnowledgeBaseApi.EmbeddingModel,
        };

        var inspection = (await api.InspectKnowledgeBaseAsync(draft.ToInspectRequest(), Ct)).Value;

        Assert.False(KnowledgeAdminView.CanReview(inspection));
        Assert.Contains(inspection.Warnings, w => KnowledgeAdminView.LevelOf(w) == WarningLevel.Error);
    }

    [Fact]
    public async Task AnAdminConfiguresAnEditsAndDeletesARagToolThroughTheClient()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var api = new QuorumApi(setup.Admin);
        var knowledgeBaseId = await setup.RegisterAsync("policies");
        var dictionary = (await api.ListDictionaryAsync(Ct)).Value;
        var detail = (await api.GetKnowledgeBaseAsync(knowledgeBaseId, Ct)).Value;
        var details = new Dictionary<Guid, KnowledgeBaseDetail> { [knowledgeBaseId] = detail };

        // The editor offers the fields the knowledge base maps and exposes them all by default.
        var form = new RagToolForm { Name = "company", Description = "Searches the company policies." };
        form.KnowledgeBaseIds.Add(knowledgeBaseId);
        var choices = RagToolView.Candidates(form.KnowledgeBaseIds, details, dictionary);
        form.ApplyCandidates(choices.Fields, exposeNew: choices.Complete);
        Assert.True(choices.Complete);
        Assert.Equal(["confidential", "department", "published_on", "year"], choices.Fields.Select(f => f.Name));
        Assert.Equal(choices.Fields.Count, form.ExposedFieldIds.Count);

        // A fixed filter the model never sees.
        var department = dictionary.Single(f => f.Name == "department");
        form.AddFixedFilter(department.Id);
        form.FilterValues["department"] = "hr";
        Assert.Empty(form.Problems(dictionary));

        var created = (await api.CreateRagToolAsync(form.ToRequest(dictionary), Ct)).Value;
        Assert.Equal("rag__company", created.Definition!.Name);
        Assert.True(created.Definition.EstimatedTokens > 0);

        // The editor reads the saved tool back into the same form, with the definition preview.
        var saved = (await api.GetRagToolAsync(created.Id, Ct)).Value;
        var reloaded = RagToolForm.From(saved, dictionary);
        Assert.Equal(form.ExposedFieldIds.Order(), reloaded.ExposedFieldIds.Order());
        Assert.Equal([department.Id], reloaded.FixedFilterFieldIds);
        Assert.Equal("hr", reloaded.FilterValues["department"]);
        Assert.Equal(ToolPolicies.Auto, reloaded.Policy);
        Assert.Contains("year_min", saved.Definition!.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            RagToolView.ParameterNames(choices.Fields, 1).Order(StringComparer.Ordinal),
            // The editor does not know the document_ids switch yet (spec 007 PR 9b); the server turns it on for a tool that leaves it out.
            saved.Definition.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => n != "document_ids").Order(StringComparer.Ordinal));

        reloaded.Policy = ToolPolicies.Confirm;
        reloaded.TopK = 3;
        Assert.True((await api.UpdateRagToolAsync(created.Id, reloaded.ToRequest(dictionary), Ct)).IsSuccess);
        var updated = (await api.GetRagToolAsync(created.Id, Ct)).Value;
        Assert.Equal((ToolPolicies.Confirm, 3), (updated.Policy, updated.TopK));

        // It is listed with its knowledge base, and the server refuses a second tool with the same name.
        var listed = Assert.Single((await api.ListRagToolsAsync(Ct)).Value);
        Assert.Equal(["policies"], listed.KnowledgeBases.Select(k => k.Name));
        Assert.Equal("rag.name_taken", (await api.CreateRagToolAsync(form.ToRequest(dictionary), Ct)).Problem!.Code);

        // The tool row of the page changes state and policy through the ordinary tools route.
        Assert.True((await api.UpdateToolAsync(created.Id, ToolStates.Disabled, ToolPolicies.Confirm, Ct)).IsSuccess);
        Assert.Equal(ToolStates.Disabled, Assert.Single((await api.ListRagToolsAsync(Ct)).Value).State);

        Assert.True((await api.DeleteRagToolAsync(created.Id, Ct)).IsSuccess);
        Assert.Empty((await api.ListRagToolsAsync(Ct)).Value);
    }

    // The flat sample with `year` written as text: the same field name as the numeric one, another meaning and type.
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

    [Fact] // AC-52 to AC-58
    public async Task TwoCollectionsThatShareAFieldNameWithDifferentTypesAreRegisteredAndExposedUnderAliases()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var api = new QuorumApi(setup.Admin);
        var numeric = await qdrant.SeedSharedAsync(SampleCollections.CreateFlat);
        var textual = await qdrant.SeedSharedAsync(TextYear);

        // The first collection: `year` is a number, and the admin puts it in a cluster of its own.
        var finance = new KnowledgeBaseDraft { Name = "finance-docs", Description = "Finance.", Url = setup.Url, Collection = numeric, EmbeddingModel = KnowledgeBaseApi.EmbeddingModel };
        finance.UseInspection((await api.InspectKnowledgeBaseAsync(finance.ToInspectRequest(), Ct)).Value);
        finance.Mapping!.Rows.Single(r => r.Name == "year").Cluster = "finance";
        finance.Mapping.ConfirmCreate = true;
        var dictionary = (await api.ListDictionaryAsync(Ct)).Value;
        Assert.Empty(finance.Problems(dictionary));
        var financeId = (await api.CreateKnowledgeBaseAsync(finance.ToRequest(dictionary), Ct)).Value.Id;

        // The second one: `year` is text. The only field called year is the number, so it is preselected with a type conflict ...
        var hr = new KnowledgeBaseDraft { Name = "hr-docs", Description = "HR.", Url = setup.Url, Collection = textual, EmbeddingModel = KnowledgeBaseApi.EmbeddingModel };
        hr.UseInspection((await api.InspectKnowledgeBaseAsync(hr.ToInspectRequest(), Ct)).Value);
        dictionary = (await api.ListDictionaryAsync(Ct)).Value;
        var year = hr.Mapping!.Rows.Single(r => r.Name == "year");
        Assert.Equal("finance", year.Cluster);
        Assert.Contains(hr.Mapping.Review(dictionary).Single(r => r.Row == year).Problems, p => p.Contains("another cluster", StringComparison.Ordinal));

        // ... and moving it to a cluster of its own makes it a new field of its own type.
        year.Cluster = "hr";
        hr.Mapping.ConfirmCreate = true;
        Assert.Empty(hr.Problems(dictionary));
        var hrId = (await api.CreateKnowledgeBaseAsync(hr.ToRequest(dictionary), Ct)).Value.Id;
        dictionary = (await api.ListDictionaryAsync(Ct)).Value;
        Assert.Equal(["finance/year", "hr/year"], dictionary.Where(f => f.Name == "year").Select(f => $"{f.Cluster}/{f.Name}").Order());

        // A rag tool over both: the two years collide until one gets an alias, then the definition has the parameters of both.
        var details = new Dictionary<Guid, KnowledgeBaseDetail>
        {
            [financeId] = (await api.GetKnowledgeBaseAsync(financeId, Ct)).Value,
            [hrId] = (await api.GetKnowledgeBaseAsync(hrId, Ct)).Value,
        };
        var form = new RagToolForm { Name = "company", Description = "Searches the company documents." };
        form.KnowledgeBaseIds.AddRange([financeId, hrId]);
        var choices = RagToolView.Candidates(form.KnowledgeBaseIds, details, dictionary);
        form.ApplyCandidates(choices.Fields, exposeNew: true);
        Assert.Contains(form.Problems(dictionary), p => p.Contains("both be called \"year\"", StringComparison.Ordinal));

        form.SetAlias(dictionary.Single(f => f is { Name: "year", Cluster: "hr" }), "year_hr");
        Assert.Empty(form.Problems(dictionary));
        var created = (await api.CreateRagToolAsync(form.ToRequest(dictionary), Ct)).Value;

        var parameters = created.Definition!.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("year_hr", parameters);
        Assert.Contains("year_min", parameters);
        Assert.Contains("year_max", parameters);
        var saved = (await api.GetRagToolAsync(created.Id, Ct)).Value;
        var reloaded = RagToolForm.From(saved, dictionary, choices.Fields);
        Assert.Equal("year_hr", Assert.Single(reloaded.Aliases).Value);
    }
}
