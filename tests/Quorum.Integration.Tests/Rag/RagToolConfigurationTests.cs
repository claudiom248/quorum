using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.KnowledgeBases.Search;
using Quorum.Integration.Tests.Tools;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.Rag;

/// <summary>Configuring a rag tool: the definition generated from its binding, the limits, the rule between scopes (spec 003, AC-19 to AC-23).</summary>
public sealed class RagToolConfigurationTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Hr = ["hr"];
    private static readonly string[] HrAndLegal = ["hr", "legal"];

    private static string[] Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];

    [Fact] // AC-19
    public async Task TheDefinitionHasTheQueryAndTheFiltersOfTheExposedFieldsAndNoKnowledgeBaseParameterForASingleOne()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();

        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: [fields["department"], fields["year"]]));

        var definition = created.Definition!;
        Assert.Equal("rag__company", definition.Name);
        Assert.Equal(["department", "document_ids", "query", "year_max", "year_min"], Sorted(definition.Parameters));
        Assert.Equal(["query"], definition.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.False(definition.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("Searches the company policies.", definition.Description);
        Assert.Contains("policies: Policies about policies", definition.Description);
        Assert.True(definition.EstimatedTokens > 0);
    }

    [Fact] // AC-19, D-11
    public async Task SeveralKnowledgeBasesAddAParameterToChooseOneByName()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var beta = await setup.RegisterAsync("beta");
        var alpha = await setup.RegisterAsync("alpha");

        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [beta, alpha]));

        var parameter = created.Definition!.Parameter("knowledge_base");
        Assert.Equal(["alpha", "beta"], parameter.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["document_ids", "knowledge_base", "query"], Sorted(created.Definition.Parameters));
    }

    [Fact] // AC-20
    public async Task EachTypeOfFieldGetsItsOwnParameters()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var flat = await setup.RegisterAsync("flat");
        var tags = await setup.RegisterAsync("tagged", SampleCollections.CreateTags, SearchApi.TagsMapping, SearchApi.TagsCreateFields);
        var fields = await setup.FieldsAsync();

        var created = await RagApi.CreateOkAsync(
            setup.Admin,
            RagApi.Body(knowledgeBaseIds: [flat, tags], exposedFieldIds: [fields["department"], fields["year"], fields["published_on"], fields["confidential"], fields["tags"]]));

        var definition = created.Definition!;
        Assert.Equal(
            ["confidential", "department", "document_ids", "knowledge_base", "published_on_from", "published_on_to", "query", "tags", "year_max", "year_min"],
            Sorted(definition.Parameters));
        Assert.Equal(("array", "string"), (definition.Parameter("department").GetProperty("type").GetString(), definition.Parameter("department").GetProperty("items").GetProperty("type").GetString()));
        Assert.Equal(("array", "string"), (definition.Parameter("tags").GetProperty("type").GetString(), definition.Parameter("tags").GetProperty("items").GetProperty("type").GetString()));
        Assert.Equal("boolean", definition.Parameter("confidential").GetProperty("type").GetString());
        Assert.Equal(("number", "number"), (definition.Parameter("year_min").GetProperty("type").GetString(), definition.Parameter("year_max").GetProperty("type").GetString()));
        Assert.Equal(("string", "string"), (definition.Parameter("published_on_from").GetProperty("type").GetString(), definition.Parameter("published_on_to").GetProperty("type").GetString()));
    }

    [Fact] // D-11
    public async Task TheDescriptionOfAFieldReachesTheModel()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();
        using var described = await setup.Admin.PutAsJsonAsync(DictionaryApi.Field(fields["department"]), new { description = "The owning department, like hr or finance" }, Ct);
        described.EnsureSuccessStatusCode();

        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: [fields["department"]]));

        Assert.Contains("The owning department, like hr or finance", created.Definition!.Parameter("department").GetProperty("description").GetString());
    }

    [Fact] // AC-23
    public async Task ANewRagToolIsEnabledWithTheAutoPolicyAndListedAmongTheToolsWithOriginRag()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));

        var tool = Assert.Single(await ToolsApi.ListToolsAsync(setup.Admin), t => t.Origin == "Rag");
        var pool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool;
        var listed = Assert.Single(await RagApi.ListAsync(setup.Admin));

        Assert.Equal((created.Id, "company", "Enabled", "Auto"), (tool.Id, tool.Name, tool.State, tool.Policy));
        Assert.Equal("Searches the company policies.", tool.Description);
        // In the pool, but off until an administrator activates it on the assistant.
        Assert.False(Assert.Single(pool, t => t.Name == "rag__company").Active);
        Assert.Equal((created.Id, "company", "Tenant", false), (listed.Id, listed.Name, listed.Scope.Kind, listed.Shared));
        Assert.Equal(["policies"], listed.KnowledgeBases.Select(k => k.Name));
    }

    [Fact] // AC-23
    public async Task TheAdministratorCanAskForConfirmationFromTheStart()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], policy: "Confirm"));

        Assert.Equal("Confirm", (await RagApi.GetAsync(setup.Admin, created.Id)).Policy);
    }

    [Fact]
    public async Task TheDetailShowsTheConfigurationAndTheDefinitionTheModelWouldGet()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();
        var created = await RagApi.CreateOkAsync(
            setup.Admin,
            RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: [fields["year"]], fixedFilters: new { department = Hr }, topK: 7, scoreThreshold: 0.3, maxResultCharacters: 2500));

        var detail = await RagApi.GetAsync(setup.Admin, created.Id);

        Assert.Equal([kb], detail.KnowledgeBaseIds);
        Assert.Equal([fields["year"]], detail.ExposedFieldIds);
        Assert.Equal((7, 0.3, 2500), (detail.TopK, detail.ScoreThreshold.GetValueOrDefault(), detail.MaxResultCharacters));
        Assert.Equal("hr", detail.FixedFilters.GetProperty("department")[0].GetString());
        // A fixed filter is never part of what the model sees.
        Assert.DoesNotContain("department", detail.Definition!.Parameters);
        Assert.Equal(created.Definition!.InputSchema.GetRawText(), detail.Definition.InputSchema.GetRawText());
    }

    [Fact]
    public async Task TheDefaultLimitsApplyWhenTheRequestLeavesThemOut()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));

        var detail = await RagApi.GetAsync(setup.Admin, created.Id);
        Assert.Equal((5, (double?)null, 6000), (detail.TopK, detail.ScoreThreshold, detail.MaxResultCharacters));
    }

    [Theory] // AC-21
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("this-name-is-longer-than-forty-characters-for-sure")]
    public async Task AnInvalidNameIsRejected(string name)
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(name: name, knowledgeBaseIds: [kb]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.invalid_name", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ADescriptionIsRequired()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(description: "  ", knowledgeBaseIds: [kb]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.invalid_description", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-21
    public async Task MoreThanTenKnowledgeBasesAreRejected()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: Enumerable.Range(0, 11).Select(_ => Guid.NewGuid())));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.too_many_knowledge_bases", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AToolWithoutKnowledgeBasesIsRejected()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.no_knowledge_base", await ToolsApi.ProblemCodeAsync(response));
    }

    [Theory] // AC-21
    [InlineData(21, null, null)]
    [InlineData(0, null, null)]
    [InlineData(null, null, 199)]
    [InlineData(null, null, 50_001)]
    public async Task LimitsOutsideTheCeilingsAreRejected(int? topK, double? threshold, int? cap)
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], topK: topK, scoreThreshold: threshold, maxResultCharacters: cap));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.limit_exceeded", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-21
    public async Task MoreThanTenExposedFieldsAreRejected()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: Enumerable.Range(0, 11).Select(_ => Guid.NewGuid())));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.limit_exceeded", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AFieldTheKnowledgeBasesDoNotMapCannotBeExposed()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var region = await DictionaryApi.CreateOkAsync(setup.Admin, "region", "Text");

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: [region]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.invalid_filter", await ToolsApi.ProblemCodeAsync(response));
    }

    [Theory] // AC-21
    [InlineData("""{"nothing":["x"]}""")] // not a filter
    [InlineData("""{"department":[]}""")] // a fixed filter that restricts nothing would widen access
    [InlineData("""{"department":""}""")]
    [InlineData("""{"year_min":"soon"}""")] // does not fit the type
    [InlineData("""{"published_on_from":"yesterday"}""")]
    [InlineData("""{"confidential":"yes"}""")]
    [InlineData("""{"year":5}""")] // a number is filtered with _min and _max
    [InlineData("""["department"]""")] // not an object
    public async Task AFixedFilterThatDoesNotFitItsFieldIsRejected(string filters)
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        using var document = JsonDocument.Parse(filters);

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], fixedFilters: document.RootElement.Clone()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.invalid_filter", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact] // D-11
    public async Task AFixedFilterOnAFieldThatIsNotExposedIsAccepted()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        var created = await RagApi.CreateOkAsync(
            setup.Admin,
            RagApi.Body(knowledgeBaseIds: [kb], fixedFilters: new { department = HrAndLegal, year_min = 2023, published_on_to = "2025-12-31", confidential = false }));

        Assert.DoesNotContain("department", created.Definition!.Parameters);
    }

    [Fact] // AC-22
    public async Task AGlobalToolCannotIncludeAKnowledgeBaseOnlyTheTenantSees()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var tenantOnly = await setup.RegisterAsync("tenant-only", createFields: [], mapping: KnowledgeBaseApi.TextOnly());

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [tenantOnly], scope: new { kind = "Global" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.knowledge_base_not_visible", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-22
    public async Task ATenantToolCannotIncludeAKnowledgeBaseOfSomeAssistantsOnly()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var assistantOnly = await setup.RegisterAsync("assistant-only", scope: new { kind = "Assistant", assistantIds = new[] { setup.AssistantId } });

        using var wide = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [assistantOnly]));
        var narrow = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [assistantOnly], scope: new { kind = "Assistant", assistantIds = new[] { setup.AssistantId } }));

        Assert.Equal("rag.knowledge_base_not_visible", await ToolsApi.ProblemCodeAsync(wide));
        Assert.Equal(HttpStatusCode.Created, narrow.StatusCode);
        narrow.Dispose();
    }

    [Fact]
    public async Task AKnowledgeBaseThatDoesNotExistIsNotVisible()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("rag.knowledge_base_not_visible", await ToolsApi.ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Everyone")]
    [InlineData("Agent")]
    public async Task AnInvalidScopeIsRejectedLikeTheOneOfAServer(string kind)
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], scope: new { kind }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(kind == "Agent" ? "tool.scope_not_supported" : "tool.invalid_scope", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AnAssistantThatDoesNotExistCannotBeInTheScope()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        using var response = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], scope: new { kind = "Assistant", assistantIds = new[] { Guid.NewGuid() } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("tool.invalid_scope", await ToolsApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheNameOfARagToolIsUniqueButMayBeThatOfABuiltinTool()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));

        using var again = await RagApi.CreateAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));
        // Their model-facing names differ (rag__ and builtin__), and so do their audit entries.
        using var sameAsBuiltin = await RagApi.CreateAsync(setup.Admin, RagApi.Body(name: "calculator", knowledgeBaseIds: [kb]));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("rag.name_taken", await ToolsApi.ProblemCodeAsync(again));
        Assert.Equal(HttpStatusCode.Created, sameAsBuiltin.StatusCode);
    }

    [Fact]
    public async Task UpdatingARagToolChangesItsDefinitionAndKeepsItsActivation()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var fields = await setup.FieldsAsync();
        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb], exposedFieldIds: [fields["year"]]));
        await RagApi.ActivateAsync(setup.Admin, "rag__company");

        using var update = await setup.Admin.PutAsJsonAsync(
            RagApi.One(created.Id),
            RagApi.Body(description: "Searches policies by department.", knowledgeBaseIds: [kb], exposedFieldIds: [fields["department"]], topK: 3),
            Ct);
        var detail = await RagApi.GetAsync(setup.Admin, created.Id);
        var pool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool;

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(["department", "document_ids", "query"], Sorted(detail.Definition!.Parameters));
        Assert.Equal(("Searches policies by department.", 3), (detail.Description, detail.TopK));
        Assert.True(Assert.Single(pool, t => t.Name == "rag__company").Active);
    }

    [Fact]
    public async Task ARagToolCanBeRenamedAndItsStateChangedThroughTheToolRoutes()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));

        using var rename = await setup.Admin.PutAsJsonAsync(RagApi.One(created.Id), RagApi.Body(name: "handbook", knowledgeBaseIds: [kb]), Ct);
        using var disable = await setup.Admin.PutAsJsonAsync(ToolsApi.Tool(created.Id), new { state = "Disabled", policy = "Confirm" }, Ct);
        var pool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool;
        var listed = Assert.Single(await RagApi.ListAsync(setup.Admin));

        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        Assert.Equal(("handbook", "Disabled", "Confirm"), (listed.Name, listed.State, listed.Policy));
        Assert.DoesNotContain(pool, t => t.Name.StartsWith("rag__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdatingToANameAnotherRagToolHasIsRejected()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(name: "first", knowledgeBaseIds: [kb]));
        var second = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(name: "second", knowledgeBaseIds: [kb]));

        using var rename = await setup.Admin.PutAsJsonAsync(RagApi.One(second.Id), RagApi.Body(name: "first", knowledgeBaseIds: [kb]), Ct);

        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal("rag.name_taken", await ToolsApi.ProblemCodeAsync(rename));
    }

    [Fact]
    public async Task DeletingARagToolRemovesItsActivationAndNothingElse()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var created = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(knowledgeBaseIds: [kb]));
        await RagApi.ActivateAsync(setup.Admin, "rag__company", "builtin__calculator");

        using var delete = await setup.Admin.DeleteAsync(RagApi.One(created.Id), Ct);
        using var read = await setup.Admin.GetAsync(RagApi.One(created.Id), Ct);
        var pool = (await ToolsApi.GetAssistantToolsAsync(setup.Admin, setup.AssistantId)).Pool;

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Empty(await RagApi.ListAsync(setup.Admin));
        Assert.DoesNotContain(pool, t => t.Name == "rag__company");
        Assert.True(Assert.Single(pool, t => t.Name == "builtin__calculator").Active);
        Assert.Equal(0L, await ToolFixtures.ScalarAsync<long>(setup.App, "SELECT count(*) FROM agents.rag_tools"));
        Assert.Equal(0L, await ToolFixtures.ScalarAsync<long>(setup.App, "SELECT count(*) FROM agents.assistant_tools a JOIN agents.tools t ON t.\"Id\" = a.\"ToolId\" WHERE t.\"Origin\" = 'Rag'"));
        // The knowledge base is only referenced.
        Assert.Single(await KnowledgeBaseApi.ListAsync(setup.Admin));
    }

    [Fact]
    public async Task ARagToolThatDoesNotExistIsNotFound()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");

        using var read = await setup.Admin.GetAsync(RagApi.One(Guid.NewGuid()), Ct);
        using var update = await setup.Admin.PutAsJsonAsync(RagApi.One(Guid.NewGuid()), RagApi.Body(knowledgeBaseIds: [kb]), Ct);
        using var delete = await setup.Admin.DeleteAsync(RagApi.One(Guid.NewGuid()), Ct);

        Assert.All([read, update, delete], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    [Fact]
    public async Task OnlyRagToolsAreReachableThroughTheRagRoutes()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var calculator = (await ToolsApi.ListToolsAsync(setup.Admin)).First(t => t.Origin == "Builtin");

        using var update = await setup.Admin.PutAsJsonAsync(RagApi.One(calculator.Id), RagApi.Body(knowledgeBaseIds: [kb]), Ct);
        using var delete = await setup.Admin.DeleteAsync(RagApi.One(calculator.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Contains(await ToolsApi.ListToolsAsync(setup.Admin), t => t.Id == calculator.Id);
    }
}
