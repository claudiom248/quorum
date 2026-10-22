using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>Manual lookups: values the administrator types, with a scope of their own (spec 007 D-5).</summary>
public sealed class ManualLookupTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AManualLookupIsCreatedListedAndReadBackWithLabelsAndSynonyms()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var id = await LookupApi.CreateManualOkAsync(admin, LookupApi.ManualBody(description: "Where people come from."));

        var listed = Assert.Single(await LookupApi.ListManualAsync(admin));
        Assert.Equal((id, "nationality", "Where people come from.", "Tenant", 2, false), (listed.Id, listed.Name, listed.Description, listed.Scope, listed.ValueCount, listed.Shared));

        var values = await LookupApi.ReadValuesOkAsync(admin, id);
        Assert.False(values.Stale);
        Assert.NotNull(values.AsOf);
        Assert.Null(values.Unit);
        var italy = Assert.Single(values.Values, v => v.Value == "IT");
        Assert.Equal("Italy", italy.Label);
        Assert.Equal(["Italia", "italiana"], italy.Synonyms!);
        Assert.Null(italy.Count);
    }

    [Fact] // AC-11
    public async Task ATenantScopeIsInvisibleToOtherTenantsAndAGlobalOneIsSharedReadOnly()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var tenantId = await TenantScope.TenantIdAsync(app);
        var listed = await LookupApi.SeedManualOfAnotherTenantAsync(app, "listed", LookupScope.Tenant, tenantId);
        var hidden = await LookupApi.SeedManualOfAnotherTenantAsync(app, "hidden", LookupScope.Tenant, Guid.NewGuid());
        var shared = await LookupApi.SeedManualOfAnotherTenantAsync(app, "shared", LookupScope.Global);
        var own = await LookupApi.CreateManualOkAsync(admin, LookupApi.ManualBody("own"));

        var lookups = await LookupApi.ListManualAsync(admin);

        Assert.Equal(
            [("listed", true), ("own", false), ("shared", true)],
            lookups.OrderBy(l => l.Name, StringComparer.Ordinal).Select(l => (l.Name, l.Shared)).ToArray());
        Assert.All(lookups.Where(l => l.Shared), l => Assert.Null(l.ScopeTenantIds));
        Assert.DoesNotContain(lookups, l => l.Id == hidden);

        // What others share is readable but never writable.
        foreach (var other in new[] { listed, shared })
        {
            using var update = await admin.PutAsJsonAsync(LookupApi.ManualLookup(other), LookupApi.ManualBody("taken_over"), Ct);
            using var delete = await admin.DeleteAsync(LookupApi.ManualLookup(other), Ct);
            Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        Assert.Equal("secret-value", Assert.Single((await LookupApi.ReadValuesOkAsync(admin, shared)).Values).Value);
        using var read = await admin.GetAsync(LookupApi.Values(hidden), Ct);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(2, (await LookupApi.ReadValuesOkAsync(admin, own)).Values.Count);
    }

    [Fact] // AC-11
    public async Task ANameAlreadyUsedByTheTenantIsRefusedOnCreateAndRename()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await LookupApi.CreateManualOkAsync(admin, LookupApi.ManualBody("nationality"));
        var other = await LookupApi.CreateManualOkAsync(admin, LookupApi.ManualBody("audience"));

        using var duplicate = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody("nationality"));
        using var rename = await admin.PutAsJsonAsync(LookupApi.ManualLookup(other), LookupApi.ManualBody("nationality"), Ct);
        using var keep = await admin.PutAsJsonAsync(LookupApi.ManualLookup(other), LookupApi.ManualBody("audience", description: "Who reads it."), Ct);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("lookup.name_taken", await LookupApi.ProblemCodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal("lookup.name_taken", await LookupApi.ProblemCodeAsync(rename));
        Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);
    }

    [Fact] // AC-11
    public async Task TheLimitOfFiveThousandValuesIsInclusive()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        object[] Many(int count) => [.. Enumerable.Range(0, count).Select(i => LookupApi.Value($"value-{i}"))];

        using var tooMany = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody("too_many", values: Many(5001)));
        using var atTheLimit = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody("at_the_limit", values: Many(5000)));

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("lookup.too_many_values", await LookupApi.ProblemCodeAsync(tooMany));
        Assert.Equal(HttpStatusCode.Created, atTheLimit.StatusCode);
        Assert.Equal(5000, Assert.Single(await LookupApi.ListManualAsync(admin)).ValueCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Nationality")]
    [InlineData("1nationality")]
    [InlineData("nation-ality")]
    [InlineData("a_name_that_is_longer_than_forty_characters_long")]
    public async Task ANameThatIsNotValidIsRefused(string? name)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody(name!));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup.invalid_name", await LookupApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ADescriptionLongerThanFiveHundredCharactersIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody(description: new string('d', 501)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup.invalid_description", await LookupApi.ProblemCodeAsync(response));
    }

    public static TheoryData<string, object[]> InvalidValues => new()
    {
        { "an empty value", [LookupApi.Value("  ")] },
        { "a missing value", [LookupApi.Value(null!)] },
        { "a value over 200 characters", [LookupApi.Value(new string('v', 201))] },
        { "a label over 200 characters", [LookupApi.Value("IT", new string('l', 201))] },
        { "an empty synonym", [LookupApi.Value("IT", null, [""])] },
        { "a synonym over 200 characters", [LookupApi.Value("IT", null, [new string('s', 201)])] },
        { "more than 20 synonyms", [LookupApi.Value("IT", null, [.. Enumerable.Range(0, 21).Select(i => $"s{i}")])] },
        { "the same value twice, whatever its case", [LookupApi.Value("IT"), LookupApi.Value("it")] },
    };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public async Task AValueThatIsNotValidIsRefused(string reason, object[] values)
    {
        Assert.NotEmpty(reason);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody(values: values));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup.invalid_value", await LookupApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task AValueOfExactlyTwoHundredCharactersAndAnEmptyListAreAccepted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var longest = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody("longest", values: [LookupApi.Value(new string('v', 200))]));
        using var empty = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody("empty", values: []));

        Assert.Equal(HttpStatusCode.Created, longest.StatusCode);
        Assert.Equal(HttpStatusCode.Created, empty.StatusCode);
    }

    [Theory]
    [InlineData("Nobody", null)]
    [InlineData("Global", "tenant")]
    public async Task AScopeThatDoesNotFitIsRefused(string scope, string? withTenant)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        Guid[]? tenants = withTenant is null ? null : [Guid.NewGuid()];

        using var response = await LookupApi.CreateManualAsync(admin, LookupApi.ManualBody(scope: scope, scopeTenantIds: tenants));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lookup.invalid_scope", await LookupApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task UpdatingReplacesTheValuesAsANewGenerationAndDropsTheOldRows()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await LookupApi.CreateManualOkAsync(admin);
        var tenant = Guid.NewGuid();

        using var update = await admin.PutAsJsonAsync(
            LookupApi.ManualLookup(id),
            LookupApi.ManualBody("nationality", "Changed.", "Tenant", [tenant], [LookupApi.Value("DE", "Germany", ["Deutschland"])]),
            Ct);

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var values = await LookupApi.ReadValuesOkAsync(admin, id);
        var germany = Assert.Single(values.Values);
        Assert.Equal(("DE", "Germany"), (germany.Value, germany.Label));
        Assert.Equal(["Deutschland"], germany.Synonyms!);
        var listed = Assert.Single(await LookupApi.ListManualAsync(admin));
        Assert.Equal(("Changed.", 1), (listed.Description, listed.ValueCount));
        Assert.Equal([tenant], listed.ScopeTenantIds!);
        Assert.Equal(1, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM knowledge_base.lookup_values"));
        Assert.Equal(2, await ToolFixtures.ScalarAsync<int>(app, "SELECT \"CurrentGeneration\" FROM knowledge_base.lookup_slices"));
    }

    [Fact]
    public async Task UpdatingWithInvalidValuesKeepsTheCurrentOnes()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await LookupApi.CreateManualOkAsync(admin);

        using var update = await admin.PutAsJsonAsync(LookupApi.ManualLookup(id), LookupApi.ManualBody(values: [LookupApi.Value("")]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal(2, (await LookupApi.ReadValuesOkAsync(admin, id)).Values.Count);
    }

    [Fact]
    public async Task UpdatingAndDeletingAnUnknownLookupReturnsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var unknown = Guid.NewGuid();

        using var update = await admin.PutAsJsonAsync(LookupApi.ManualLookup(unknown), LookupApi.ManualBody(), Ct);
        using var delete = await admin.DeleteAsync(LookupApi.ManualLookup(unknown), Ct);

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task DeletingRemovesTheLookupItsSliceAndItsValues()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await LookupApi.CreateManualOkAsync(admin);
        var kept = await LookupApi.CreateManualOkAsync(admin, LookupApi.ManualBody("kept", values: [LookupApi.Value("A")]));

        using var delete = await admin.DeleteAsync(LookupApi.ManualLookup(id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal([kept], (await LookupApi.ListManualAsync(admin)).Select(l => l.Id).ToArray());
        Assert.Equal(1, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM knowledge_base.lookup_slices"));
        Assert.Equal(1, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM knowledge_base.lookup_values"));
    }

    [Fact]
    public async Task ALookupThatToolsUseCannotBeDeleted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await LookupApi.CreateManualOkAsync(admin);
        await ToolFixtures.ExecuteAsync(app, $"UPDATE knowledge_base.lookup_slices SET \"ToolIds\" = ARRAY['{Guid.NewGuid()}']::uuid[]");

        using var delete = await admin.DeleteAsync(LookupApi.ManualLookup(id), Ct);

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("lookup.in_use", await LookupApi.ProblemCodeAsync(delete));
        Assert.Single(await LookupApi.ListManualAsync(admin));
    }
}
