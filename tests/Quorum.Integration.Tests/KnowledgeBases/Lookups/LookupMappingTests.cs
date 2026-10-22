using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>The members spec 007 adds to the mapping of a knowledge base: listable fields, the counted point, the entity name and the refresh interval.</summary>
public sealed class LookupMappingTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Mapping(object? countedPoint = null, string? entityName = null, params (string Path, string Name, bool Listable)[] fields) => new
    {
        roles = new { text = "text", title = "title" },
        fields = fields.Select(f => new { path = f.Path, name = f.Name, listable = f.Listable }).ToArray(),
        countedPoint,
        entityName,
    };

    private async Task<(QuorumApp App, HttpClient Admin, string Collection, string Url)> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        return (app, admin, collection, (await qdrant.GetUrlAsync()).ToString());
    }

    [Fact] // AC-2
    public async Task ATextFieldCanBeMarkedListableAndTheDetailSaysSo()
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, mapping: Mapping(fields: [("department", "department", true), ("year", "year", false)]));

        var fields = (await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Mapping!.Fields.ToDictionary(f => f.Path);
        Assert.True(fields["department"].Listable);
        Assert.False(fields["year"].Listable);
    }

    [Theory] // AC-2
    [InlineData("year")]
    [InlineData("confidential")]
    [InlineData("published_on")]
    public async Task ANumberBooleanOrDateFieldCannotBeMarkedListable(string path)
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: Mapping(fields: [(path, path, true)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_listable_field", await KnowledgeBaseApi.ProblemCodeAsync(response));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
    }

    [Fact] // AC-2
    public async Task ABooleanFieldMappedAsTheCountedPointIsStoredWithItsValue()
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await KnowledgeBaseApi.RegisterOkAsync(
            admin,
            url,
            collection,
            mapping: Mapping(new { path = "confidential", value = true }, fields: [("confidential", "confidential", false)]));

        var point = (await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Mapping!.CountedPoint!;
        Assert.Equal("confidential", point.Path);
        Assert.Equal(JsonValueKind.True, point.Value.ValueKind);
    }

    [Fact] // AC-2
    public async Task AFieldThatIsNotMappedCanBeTheCountedPointWhenTheSampledValuesFit()
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, mapping: Mapping(new { path = "page", value = 1 }));

        var point = (await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Mapping!.CountedPoint!;
        Assert.Equal(("page", 1), (point.Path, point.Value.GetInt32()));
    }

    [Fact]
    public async Task ATextCountedPointKeepsItsType()
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, mapping: Mapping(new { path = "department", value = "hr" }, fields: [("department", "department", false)]));

        var point = (await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Mapping!.CountedPoint!;
        Assert.Equal(("department", JsonValueKind.String, "hr"), (point.Path, point.Value.ValueKind, point.Value.GetString()));
    }

    [Theory] // AC-2
    [InlineData("confidential", true, "yes")]
    [InlineData("confidential", true, false)]
    [InlineData("year", true, "2021")]
    [InlineData("department", true, 3)]
    [InlineData("published_on", true, "2024-01-01")]
    [InlineData("page", false, "first")]
    [InlineData("page", false, null)]
    [InlineData("page", false, "")]
    public async Task ACountedPointWhoseValueDoesNotFitItsFieldIsRefused(string path, bool mapped, object? value)
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        (string, string, bool)[] fields = mapped ? [(path, path, false)] : [];

        using var response = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: Mapping(new { path, value }, fields: fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_counted_point", await KnowledgeBaseApi.ProblemCodeAsync(response));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
    }

    [Fact]
    public async Task ACountedPointWithoutAPathIsRefused()
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: Mapping(new { path = " ", value = true }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_counted_point", await KnowledgeBaseApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheEntityNameIsStoredAndCannotShareAPathWithAnotherRole()
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, mapping: Mapping(entityName: "document_id"));
        using var shared = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, "second", mapping: Mapping(entityName: "title"));

        Assert.Equal("document_id", (await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Mapping!.EntityName);
        Assert.Equal(HttpStatusCode.BadRequest, shared.StatusCode);
        Assert.Equal("knowledge_base.invalid_mapping", await KnowledgeBaseApi.ProblemCodeAsync(shared));
    }

    [Fact]
    public async Task AKnowledgeBaseWithoutLookupMembersHasNoneInTheDetail()
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var registered = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection);

        var mapping = (await KnowledgeBaseApi.GetAsync(admin, registered.Id)).Mapping!;
        Assert.Null(mapping.CountedPoint);
        Assert.Null(mapping.EntityName);
        Assert.All(mapping.Fields, field => Assert.False(field.Listable));
    }

    [Fact] // AC-15 (the interval)
    public async Task TheRefreshIntervalIsEveryDayUnlessSetAndCanBeChanged()
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var byDefault = await KnowledgeBaseApi.RegisterOkAsync(admin, url, collection, "by-default");
        using var weekly = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, "weekly", lookupRefreshHours: 168);

        Assert.Equal(24, (await KnowledgeBaseApi.GetAsync(admin, byDefault.Id)).LookupRefreshHours);
        Assert.Equal(HttpStatusCode.Created, weekly.StatusCode);
        var weeklyId = (await weekly.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!.Id;
        Assert.Equal(168, (await KnowledgeBaseApi.GetAsync(admin, weeklyId)).LookupRefreshHours);

        using var changed = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(byDefault.Id), KnowledgeBaseApi.Body(url, collection, "by-default", lookupRefreshHours: 1), Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(1, (await KnowledgeBaseApi.GetAsync(admin, byDefault.Id)).LookupRefreshHours);
    }

    [Fact]
    public async Task AnUpdateThatOmitsTheRefreshIntervalKeepsTheStoredOne()
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        using var created = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, lookupRefreshHours: 48);
        var id = (await created.Content.ReadFromJsonAsync<KnowledgeBaseApi.RegisteredBody>(Ct))!.Id;

        using var updated = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(id), KnowledgeBaseApi.Body(url, collection, description: "Edited."), Ct);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(48, (await KnowledgeBaseApi.GetAsync(admin, id)).LookupRefreshHours);
    }

    [Theory] // AC-15 (out of range)
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(169)]
    public async Task ARefreshIntervalOutsideOneHourToSevenDaysIsRefused(int hours)
    {
        var (app, admin, collection, url) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        using var response = await KnowledgeBaseApi.RegisterAsync(admin, url, collection, lookupRefreshHours: hours);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_refresh_interval", await KnowledgeBaseApi.ProblemCodeAsync(response));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
    }
}
