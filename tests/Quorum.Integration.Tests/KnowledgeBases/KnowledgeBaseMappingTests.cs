using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

public sealed class KnowledgeBaseMappingTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SampleDataset Custom(string name, string payload) => new(
        name,
        "A collection shaped by one test.",
        [new SampleVector(null, DeterministicEmbedding.DefaultDimensions)],
        [],
        [new SamplePoint(1, "text", JsonNode.Parse(payload)!.AsObject())]);

    private static object Roles(string? text = "text") => new { text };

    private static object Mapping(string? text, params (string Path, string Name)[] fields) =>
        new { roles = Roles(text), fields = fields.Select(f => new { path = f.Path, name = f.Name }).ToArray() };

    private async Task<(QuorumApp App, HttpClient Admin, string Collection, string Url)> StartWithFlatAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        return (app, admin, collection, (await qdrant.GetUrlAsync()).ToString());
    }

    [Theory] // AC-12
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMappingWithoutTheTextRoleIsRefused(string? text)
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: new { roles = new { text, title = "title" }, fields = Array.Empty<object>() }, createFields: []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_mapping", await KnowledgeBaseApi.ProblemCodeAsync(response));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
    }

    [Fact] // AC-12
    public async Task APathUsedTwiceIsRefused()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await KnowledgeBaseApi.RegisterAsync(
            admin,
            url,
            collection,
            mapping: Mapping("text", ("text", "body_text")),
            createFields: [new { name = "body_text", type = "Text" }]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_mapping", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-12
    public async Task ADateFieldWhoseValuesAreNotIso8601IsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var good = QdrantFixture.NewPrefix() + "good";
        var bad = QdrantFixture.NewPrefix() + "bad";
        await qdrant.SeedAsync(
        [
            Custom(good, """{"text":"t","published_on":"2024-03-10T08:30:00Z"}"""),
            Custom(bad, """{"text":"t","published_on":"last tuesday"}"""),
        ]);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var mapping = Mapping("text", ("published_on", "published_on"));
        object[] create = [new { name = "published_on", type = "Date" }];

        using var refused = await KnowledgeBaseApi.RegisterAsync(admin, url, bad, "bad", mapping: mapping, createFields: create);
        var accepted = await KnowledgeBaseApi.RegisterOkAsync(admin, url, good, "good", mapping: mapping, createFields: create);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("knowledge_base.invalid_mapping", await KnowledgeBaseApi.ProblemCodeAsync(refused));
        Assert.Equal("Available", accepted.Status);
        Assert.DoesNotContain(await DictionaryApi.ListAsync(admin), f => f.UsedByCount == 0);
    }

    [Theory] // AC-12: the payload values must fit the dictionary type
    [InlineData("Number", "\"many\"")]
    [InlineData("Boolean", "\"yes\"")]
    [InlineData("Text", "42")]
    [InlineData("TextList", "\"single\"")]
    [InlineData("TextList", "[1, 2]")]
    public async Task AFieldWhoseValuesDoNotFitItsTypeIsRefused(string type, string value)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + "misfit";
        await qdrant.SeedAsync([Custom(collection, $$"""{"text":"t","extra":{{value}}}""")]);

        using var response = await KnowledgeBaseApi.RegisterAsync(
            admin,
            (await qdrant.GetUrlAsync()).ToString(),
            collection,
            mapping: Mapping("text", ("extra", "extra")),
            createFields: [new { name = "extra", type }]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_mapping", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Fact] // AC-10
    public async Task AMissingFieldIsCreatedOnlyWhenTheAdminConfirmedIt()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var unconfirmed = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: Mapping("text", ("year", "year")), createFields: []);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal("knowledge_base.invalid_mapping", await KnowledgeBaseApi.ProblemCodeAsync(unconfirmed));
        Assert.Empty(await DictionaryApi.ListAsync(admin));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));

        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, mapping: Mapping("text", ("year", "year")), createFields: [new { name = "year", type = "Number" }]);

        var created = Assert.Single(await DictionaryApi.ListAsync(admin));
        Assert.Equal(("year", "Number", "Tenant", 1), (created.Name, created.Type, created.Scope.Kind, created.UsedByCount));
    }

    [Fact] // AC-10
    public async Task AFieldConfirmedForCreationFollowsTheScopeOfTheKnowledgeBase()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, scope: new { kind = "Global" });

        Assert.All(await DictionaryApi.ListAsync(admin), field => Assert.Equal("Global", field.Scope.Kind));
    }

    [Fact] // AC-10
    public async Task AnExistingFieldOfAnotherTypeThanTheProposedOneIsAConflictAndNothingIsSaved()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        await DictionaryApi.CreateOkAsync(admin, "year", "Text");

        using var response = await KnowledgeBaseApi.RegisterAsync(
            admin,
            url,
            collection,
            mapping: Mapping("text", ("department", "department"), ("year", "year")),
            createFields: [new { name = "department", type = "Text" }, new { name = "year", type = "Number" }]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("knowledge_base.field_type_conflict", await KnowledgeBaseApi.ProblemCodeAsync(response));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
        Assert.Equal(["year"], (await DictionaryApi.ListAsync(admin)).Select(f => f.Name));
    }

    [Fact] // AC-10
    public async Task AnExistingFieldOfTheSameTypeIsReusedNotDuplicated()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        var yearId = await DictionaryApi.CreateOkAsync(admin, "year", "Number");

        var registered = await KnowledgeBaseApi.RegisterOkAsync(
            admin,
            url,
            collection,
            mapping: Mapping("text", ("year", "year")),
            createFields: [new { name = "year", type = "Number" }]);

        var detail = await KnowledgeBaseApi.GetAsync(admin, registered.Id);
        Assert.Equal(yearId, Assert.Single(detail.Mapping!.Fields).DictionaryFieldId);
        Assert.Single(await DictionaryApi.ListAsync(admin));
    }

    [Fact]
    public async Task AFieldCanBeMappedByItsIdWithoutNamingIt()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        var yearId = await DictionaryApi.CreateOkAsync(admin, "year", "Number");

        var registered = await KnowledgeBaseApi.RegisterOkAsync(
            admin,
            url,
            collection,
            mapping: new { roles = Roles(), fields = new[] { new { path = "year", dictionaryFieldId = yearId } } },
            createFields: []);

        var field = Assert.Single((await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Mapping!.Fields);
        Assert.Equal((yearId, "year", "year"), (field.DictionaryFieldId, field.Name, field.Path));
    }

    [Fact] // AC-5
    public async Task AGlobalKnowledgeBaseCannotUseATenantField()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        await DictionaryApi.CreateOkAsync(admin, "year", "Number", scope: "Tenant");

        using var response = await KnowledgeBaseApi.RegisterAsync(
            admin,
            url,
            collection,
            mapping: Mapping("text", ("year", "year")),
            createFields: [],
            scope: new { kind = "Global" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.field_not_visible", await KnowledgeBaseApi.ProblemCodeAsync(response));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
    }

    [Fact] // AC-5
    public async Task AGlobalKnowledgeBaseCanUseAGlobalFieldAndATenantOneCanUseGlobalAndItsOwn()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        await DictionaryApi.CreateOkAsync(admin, "year", "Number", scope: "Global");
        await DictionaryApi.CreateOkAsync(admin, "department", "Text", scope: "Tenant");

        var global = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "shared", mapping: Mapping("text", ("year", "year")), createFields: [], scope: new { kind = "Global" });
        var tenant = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "private", mapping: Mapping("text", ("year", "year"), ("department", "department")), createFields: [], scope: new { kind = "Tenant" });

        Assert.Equal("Available", global.Status);
        Assert.Equal("Available", tenant.Status);
    }

    [Theory] // AC-5
    [InlineData("Tenant")]
    [InlineData("Global")]
    public async Task AFieldOfAnotherTenantThatIsNotVisibleCannotBeUsed(string fieldScope)
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        var foreign = await DictionaryFixtures.AddFieldOfAnotherTenantAsync(app, "foreign", Enum.Parse<DictionaryFieldScope>(fieldScope));

        using var response = await KnowledgeBaseApi.RegisterAsync(
            admin,
            url,
            collection,
            mapping: new { roles = Roles(), fields = new[] { new { path = "department", dictionaryFieldId = foreign } } },
            createFields: []);

        // A Global field of another tenant is visible and usable by a Tenant KB; a Tenant one reads as missing.
        if (fieldScope == "Global")
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("knowledge_base.field_not_visible", await KnowledgeBaseApi.ProblemCodeAsync(response));
        }
    }

    [Fact] // AC-4 (KB part)
    public async Task AFieldInUseCountsItsKnowledgeBasesAndCannotBeDeleted()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "policies", mapping: Mapping("text", ("year", "year")), createFields: [new { name = "year", type = "Number" }]);
        var year = Assert.Single(await DictionaryApi.ListAsync(admin));

        Assert.Equal(1, year.UsedByCount);
        using var refused = await admin.DeleteAsync(DictionaryApi.Field(year.Id), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("knowledge_base.field_in_use", await DictionaryApi.ProblemCodeAsync(refused));
        Assert.Contains("policies", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var removed = await admin.DeleteAsync(KnowledgeBaseApi.One(registered.Id), Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, Assert.Single(await DictionaryApi.ListAsync(admin)).UsedByCount);
        using var now = await admin.DeleteAsync(DictionaryApi.Field(year.Id), Ct);
        Assert.Equal(HttpStatusCode.NoContent, now.StatusCode);
    }

    [Fact]
    public async Task APayloadFieldCanBeMappedToTwoKnowledgeBasesAndTheCountAddsUp()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;
        var yearId = await DictionaryApi.CreateOkAsync(admin, "year", "Number");
        var mapping = new { roles = Roles(), fields = new[] { new { path = "year", dictionaryFieldId = yearId } } };

        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "first", mapping: mapping, createFields: []);
        await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "second", mapping: mapping, createFields: []);

        Assert.Equal(2, Assert.Single(await DictionaryApi.ListAsync(admin)).UsedByCount);
    }

    [Fact]
    public async Task AQueryPrefixIsStoredAndTooLongAPrefixIsRefused()
    {
        var (app, admin, collection, url) = await StartWithFlatAsync();
        await using var _ = app;
        using var __ = admin;

        using var tooLong = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: [], queryPrefix: new string('x', 201));
        using var fine = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: KnowledgeBaseApi.TextOnly(), createFields: [], queryPrefix: "search_query: ");

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
        var id = (await fine.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!.Id;
        Assert.Equal("search_query: ", (await KnowledgeBaseApi.GetAsync(admin, id)).QueryPrefix);
    }
}
