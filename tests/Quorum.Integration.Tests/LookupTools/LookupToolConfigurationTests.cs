using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Lookups;
using Quorum.Integration.Tests.Rag;

namespace Quorum.Integration.Tests.LookupTools;

/// <summary>What an administrator can save as a lookup tool, the definition it generates and the slices it asks for (spec 007, AC-12, AC-21, AC-24, AC-25, AC-27, AC-40).</summary>
public sealed class LookupToolConfigurationTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-27
    public async Task TheDefinitionHasTheOperationsTheListsAndThePagingParametersAndNothingToFilterByWhenNoFieldIsExposed()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nationality = await LookupApi.CreateManualOkAsync(setup.Admin);
        var languages = await LookupApi.CreateManualOkAsync(setup.Admin, LookupApi.ManualBody(name: "language"));
        var (knowledgeBase, _) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");

        var created = await LookupToolApi.CreateOkAsync(
            setup.Admin,
            LookupToolApi.Body(lookups: [LookupToolApi.Bind(nationality), LookupToolApi.Bind(languages)], knowledgeBaseIds: [knowledgeBase]));

        var definition = created.Definition!;
        Assert.Equal("lookup_values__lists", definition.Name);
        Assert.Equal(["cursor", "limit", "lookup", "search", "sort"], definition.Parameters.Order().ToArray());
        // The tool queries a knowledge base, so the live operations are offered next to the stored lists.
        Assert.Equal(["lookup_values__lists", "lookup_documents__lists", "lookup_count__lists"], created.Definitions.Select(d => d.Name));
        Assert.Equal(["language", "nationality"], definition.Parameter("lookup").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Order().ToArray());
        Assert.Contains("Reads the lists of values.", definition.Description, StringComparison.Ordinal);
        Assert.True(definition.EstimatedTokens > 0);
    }

    [Fact] // AC-27
    public async Task ALookupTakesTheNameOfItsAliasInTheDefinition()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nationality = await LookupApi.CreateManualOkAsync(setup.Admin);

        var created = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(nationality, "countries")]));

        Assert.Equal(["countries"], created.Definition!.Parameter("lookup").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
        var detail = await LookupToolApi.GetAsync(setup.Admin, created.Id);
        Assert.Equal("countries", Assert.Single(detail.Lookups).Alias);
    }

    [Fact] // AC-12
    public async Task AManualLookupUsedByAToolCannotBeDeletedAndTheAnswerNamesTheTool()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nationality = await LookupApi.CreateManualOkAsync(setup.Admin);
        var tool = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(name: "countries-list", lookups: [LookupToolApi.Bind(nationality)]));

        using var refused = await setup.Admin.DeleteAsync(LookupApi.ManualLookup(nationality), Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("lookup.in_use", await LookupApi.ProblemCodeAsync(refused));
        Assert.Contains("countries-list", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var deleted = await setup.Admin.DeleteAsync(LookupToolApi.One(tool.Id), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var accepted = await setup.Admin.DeleteAsync(LookupApi.ManualLookup(nationality), Ct);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact] // AC-21
    public async Task TwoToolsWithTheSameFixedFiltersInAnyOrderShareOneSlice()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");

        var first = await LookupToolApi.CreateOkAsync(
            setup.Admin,
            LookupToolApi.Body(name: "first", lookups: [LookupToolApi.Bind(lookup)], fixedFilters: new Dictionary<string, object> { ["year_min"] = 2020, ["confidential"] = false }));
        var second = await LookupToolApi.CreateOkAsync(
            setup.Admin,
            LookupToolApi.Body(name: "second", lookups: [LookupToolApi.Bind(lookup)], fixedFilters: new Dictionary<string, object> { ["confidential"] = false, ["year_min"] = 2020 }));

        var slices = await LookupToolApi.SlicesAsync(setup.App, lookup);
        Assert.Equal(2, slices.Count);
        var shared = Assert.Single(slices, s => !s.IsWholeKnowledgeBase);
        Assert.Equal(new[] { first.Id, second.Id }.Order().ToArray(), shared.ToolIds.Order().ToArray());
    }

    [Fact] // AC-24
    public async Task ASliceGoesWhenTheLastToolThatUsesItIsChangedOrDeleted()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        var filtered = new Dictionary<string, object> { ["year_min"] = 2020 };
        var tool = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup)], fixedFilters: filtered));
        Assert.Equal(2, (await LookupToolApi.SlicesAsync(setup.App, lookup)).Count);

        using var changed = await setup.Admin.PutAsJsonAsync(LookupToolApi.One(tool.Id), LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup)], fixedFilters: new Dictionary<string, object> { ["year_min"] = 2022 }), Ct);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var afterChange = await LookupToolApi.SlicesAsync(setup.App, lookup);
        Assert.Equal(2, afterChange.Count);
        Assert.DoesNotContain("2020", afterChange.Single(s => !s.IsWholeKnowledgeBase).Filters, StringComparison.Ordinal);

        using var deleted = await setup.Admin.DeleteAsync(LookupToolApi.One(tool.Id), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var remaining = Assert.Single(await LookupToolApi.SlicesAsync(setup.App, lookup));
        Assert.True(remaining.IsWholeKnowledgeBase);
        Assert.Empty(remaining.ToolIds);
    }

    [Fact] // AC-25
    public async Task AToolThatWouldGiveAKnowledgeBaseMoreThanFiftySlicesIsRefused()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");

        // The slice of the whole knowledge base counts, so 49 different filters fill it.
        for (var year = 1; year <= 49; year++)
        {
            await LookupToolApi.CreateOkAsync(
                setup.Admin,
                LookupToolApi.Body(name: $"year-{year}", lookups: [LookupToolApi.Bind(lookup)], fixedFilters: new Dictionary<string, object> { ["year_min"] = year }));
        }

        using var refused = await LookupToolApi.CreateAsync(
            setup.Admin,
            LookupToolApi.Body(name: "one-too-many", lookups: [LookupToolApi.Bind(lookup)], fixedFilters: new Dictionary<string, object> { ["year_min"] = 50 }));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("lookup_tool.too_many_slices", await LookupToolApi.ProblemCodeAsync(refused));
        Assert.DoesNotContain(await LookupToolApi.ListAsync(setup.Admin), t => t.Name == "one-too-many");
        Assert.Equal(50, (await LookupToolApi.SlicesAsync(setup.App, lookup)).Count);
    }

    [Fact] // AC-25
    public async Task TwoToolsWithTheSameFiltersNeverCountTwice()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        for (var year = 1; year <= 49; year++)
        {
            await LookupToolApi.CreateOkAsync(
                setup.Admin,
                LookupToolApi.Body(name: $"year-{year}", lookups: [LookupToolApi.Bind(lookup)], fixedFilters: new Dictionary<string, object> { ["year_min"] = year }));
        }

        var again = await LookupToolApi.CreateOkAsync(
            setup.Admin,
            LookupToolApi.Body(name: "same-as-the-first", lookups: [LookupToolApi.Bind(lookup)], fixedFilters: new Dictionary<string, object> { ["year_min"] = 1 }));

        Assert.NotEqual(Guid.Empty, again.Id);
    }

    [Theory]
    [InlineData("Bad Name", "lookup_tool.invalid_name")]
    [InlineData("", "lookup_tool.invalid_name")]
    public async Task ANameThatIsNotAllowedIsRefused(string name, string code)
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nationality = await LookupApi.CreateManualOkAsync(setup.Admin);

        using var response = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(name: name, lookups: [LookupToolApi.Bind(nationality)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await LookupToolApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ANameTakenByAnotherLookupToolIsAConflict()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nationality = await LookupApi.CreateManualOkAsync(setup.Admin);
        await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(nationality)]));

        using var response = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(nationality)]));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("lookup_tool.name_taken", await LookupToolApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AToolBindsAtLeastOneAndAtMostTenLookups()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var many = new List<object>();
        for (var i = 0; i < 11; i++)
        {
            many.Add(LookupToolApi.Bind(await LookupApi.CreateManualOkAsync(setup.Admin, LookupApi.ManualBody(name: $"list_{i}"))));
        }

        using var none = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body());
        using var tooMany = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(lookups: [.. many]));

        Assert.Equal("lookup_tool.no_lookup", await LookupToolApi.ProblemCodeAsync(none));
        Assert.Equal("lookup_tool.too_many_lookups", await LookupToolApi.ProblemCodeAsync(tooMany));
    }

    [Fact]
    public async Task ALookupThatDoesNotExistIsNotVisible()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);

        using var response = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(Guid.NewGuid())]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup_tool.lookup_not_visible", await LookupToolApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task TwoListsOfOneNameNeedAnAliasOnOneOfThem()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var (_, computed) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        var manual = await LookupApi.CreateManualOkAsync(setup.Admin, LookupApi.ManualBody(name: "department"));

        using var clash = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(computed), LookupToolApi.Bind(manual)]));
        var fixedByAlias = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(computed), LookupToolApi.Bind(manual, "department_codes")]));

        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        Assert.Equal("lookup_tool.alias_conflict", await LookupToolApi.ProblemCodeAsync(clash));
        Assert.Equal(["department", "department_codes"], fixedByAlias.Definition!.Parameter("lookup").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Order().ToArray());
    }

    [Theory]
    [InlineData("Not Valid")]
    [InlineData("1st")]
    public async Task AnAliasThatIsNotAllowedIsRefused(string alias)
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nationality = await LookupApi.CreateManualOkAsync(setup.Admin);

        using var response = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(nationality, alias)]));

        Assert.Equal("lookup_tool.invalid_alias", await LookupToolApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AFixedFilterOnAFieldNoKnowledgeBaseOfTheToolMapsIsInvalid()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var (_, lookup) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");

        using var response = await LookupToolApi.CreateAsync(
            setup.Admin,
            LookupToolApi.Body(lookups: [LookupToolApi.Bind(lookup)], fixedFilters: new Dictionary<string, object> { ["no_such_field"] = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup_tool.invalid_filter", await LookupToolApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AGlobalToolCannotBindAManualLookupThatOnlyItsOwnerSees()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var tenantOnly = await LookupApi.CreateManualOkAsync(setup.Admin);

        using var response = await LookupToolApi.CreateAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(tenantOnly)], scope: new { kind = "Global" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup_tool.lookup_not_visible", await LookupToolApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheListAndTheDetailNameTheLookupsAndTheKnowledgeBases()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nationality = await LookupApi.CreateManualOkAsync(setup.Admin);
        var (knowledgeBase, _) = await LookupToolApi.RegisterWithLookupAsync(setup, "policies");
        var created = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(nationality)], knowledgeBaseIds: [knowledgeBase]));

        var item = Assert.Single(await LookupToolApi.ListAsync(setup.Admin));
        var detail = await LookupToolApi.GetAsync(setup.Admin, created.Id);

        Assert.Equal("nationality", Assert.Single(item.Lookups).Name);
        Assert.Equal("policies", Assert.Single(item.KnowledgeBases).Name);
        Assert.False(item.Shared);
        Assert.Equal("Enabled", item.State);
        Assert.Equal([knowledgeBase], detail.KnowledgeBaseIds);
        Assert.Equal("lookup_values__lists", detail.Definition!.Name);
    }

    [Fact] // AC-40 (the /tools/lookup half)
    public async Task ManagingLookupToolsNeedsThePermissionAndAnonymousCallersAreRejected()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nationality = await LookupApi.CreateManualOkAsync(setup.Admin);
        var existing = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(name: "existing", lookups: [LookupToolApi.Bind(nationality)]));
        using var member = await setup.App.CreateMemberClientAsync();
        using var anonymous = setup.App.CreateUiClient();
        var unknown = Guid.NewGuid();

        var requests = new List<(Func<HttpClient, Task<HttpResponseMessage>> Send, HttpStatusCode ForAdmin)>
        {
            (client => client.GetAsync(LookupToolApi.All, Ct), HttpStatusCode.OK),
            (client => client.PostAsJsonAsync(LookupToolApi.All, new { }, Ct), HttpStatusCode.BadRequest),
            (client => client.GetAsync(LookupToolApi.One(existing.Id), Ct), HttpStatusCode.OK),
            (client => client.PutAsJsonAsync(LookupToolApi.One(unknown), new { }, Ct), HttpStatusCode.NotFound),
            (client => client.DeleteAsync(LookupToolApi.One(unknown), Ct), HttpStatusCode.NotFound),
        };

        foreach (var (send, forAdmin) in requests)
        {
            using var asMember = await send(member);
            using var asAnonymous = await send(anonymous);
            using var asAdmin = await send(setup.Admin);

            Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
            Assert.Equal(forAdmin, asAdmin.StatusCode);
        }
    }

    [Fact]
    public async Task ALookupToolAppearsInTheGeneralListOfToolsWithItsOrigin()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var nationality = await LookupApi.CreateManualOkAsync(setup.Admin);
        var created = await LookupToolApi.CreateOkAsync(setup.Admin, LookupToolApi.Body(lookups: [LookupToolApi.Bind(nationality)]));

        var tools = await setup.Admin.GetFromJsonAsync<List<Tools.ToolsApi.ToolBody>>(Tools.ToolsApi.Tools, Ct);

        var tool = Assert.Single(tools!, t => t.Id == created.Id);
        Assert.Equal("Lookup", tool.Origin);
    }
}
