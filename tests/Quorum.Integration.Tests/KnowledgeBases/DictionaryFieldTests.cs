using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Integration.Tests.KnowledgeBases;

public sealed class DictionaryFieldTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private QuorumApp App => host.App;

    private HttpClient Admin => host.Admin;

    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact] // AC-1
    public async Task AFieldWithAValidNameAndTypeIsListedAsUnused()
    {
        using var response = await DictionaryApi.CreateAsync(Admin, "year", "Number", "Publication year", "Tenant");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DictionaryApi.CreatedBody>(Ct);
        var field = Assert.Single(await DictionaryApi.ListAsync(Admin));
        Assert.Equal(created!.Id, field.Id);
        Assert.Equal("year", field.Name);
        Assert.Equal("Number", field.Type);
        Assert.Equal("Publication year", field.Description);
        Assert.Equal("Tenant", field.Scope.Kind);
        Assert.Equal(0, field.UsedByCount);
    }

    [Theory] // AC-2
    [InlineData("query")]
    [InlineData("knowledge_base")]
    [InlineData("year_min")]
    [InlineData("year_max")]
    [InlineData("year_from")]
    [InlineData("year_to")]
    public async Task AReservedNameOrParameterSuffixIsRejected(string name)
    {
        using var response = await DictionaryApi.CreateAsync(Admin, name, "Number");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.reserved_field_name", await DictionaryApi.ProblemCodeAsync(response));
        Assert.Empty(await DictionaryApi.ListAsync(Admin));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Year")]
    [InlineData("1year")]
    [InlineData("_year")]
    [InlineData("pub-year")]
    [InlineData("pub year")]
    [InlineData("annoè")]
    public async Task ANameThatBreaksTheRuleIsRejected(string name)
    {
        using var response = await DictionaryApi.CreateAsync(Admin, name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_field_name", await DictionaryApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ANameUpToFortyCharactersIsAcceptedAndLongerIsNot()
    {
        await DictionaryApi.CreateOkAsync(Admin, "a" + new string('b', 39));
        using var tooLong = await DictionaryApi.CreateAsync(Admin, "a" + new string('b', 40));

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("knowledge_base.invalid_field_name", await DictionaryApi.ProblemCodeAsync(tooLong));
    }

    [Theory]
    [InlineData("Everything", "Tenant")]
    [InlineData("5", "Tenant")]
    [InlineData("Text", "Everyone")]
    [InlineData("Text", "Assistant")]
    [InlineData("Text", "")]
    [InlineData("Text,Number", "Tenant")]
    [InlineData(" Number ", "Tenant")]
    [InlineData("Text", "Tenant,Global")]
    public async Task AnUnknownTypeOrScopeIsRejected(string type, string scope)
    {
        using var response = await DictionaryApi.CreateAsync(Admin, "year", type, scope: scope);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await DictionaryApi.ListAsync(Admin));
    }

    [Fact] // AC-3
    public async Task AFieldWithTheNameOfAVisibleFieldIsRejected()
    {
        await DictionaryApi.CreateOkAsync(Admin, "year", "Number");

        using var response = await DictionaryApi.CreateAsync(Admin, "year", "Text");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("knowledge_base.field_name_taken", await DictionaryApi.ProblemCodeAsync(response));
        Assert.Equal("Number", Assert.Single(await DictionaryApi.ListAsync(Admin)).Type);
    }

    [Theory] // AC-3, D-10: a Global field of another tenant is visible, so its name is taken
    [InlineData("Tenant")]
    [InlineData("Global")]
    public async Task AGlobalFieldOfAnotherTenantIsVisibleAndItsNameIsTaken(string scope)
    {
        var shared = await DictionaryFixtures.AddFieldOfAnotherTenantAsync(App, "year", DictionaryFieldScope.Global);

        using var response = await DictionaryApi.CreateAsync(Admin, "year", scope: scope);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("knowledge_base.field_name_taken", await DictionaryApi.ProblemCodeAsync(response));
        var listed = Assert.Single(await DictionaryApi.ListAsync(Admin));
        Assert.Equal(shared, listed.Id);
        Assert.Equal("Global", listed.Scope.Kind);
    }

    [Fact] // a Global field of another tenant can be used, and says it is not the caller's to change
    public async Task AGlobalFieldOfAnotherTenantIsListedAsSharedAndAnOwnFieldIsNot()
    {
        var shared = await DictionaryFixtures.AddFieldOfAnotherTenantAsync(App, "year", DictionaryFieldScope.Global);
        var own = await DictionaryApi.CreateOkAsync(Admin, "region");

        var fields = await DictionaryApi.ListAsync(Admin);

        Assert.True(fields.Single(f => f.Id == shared).Shared);
        Assert.False(fields.Single(f => f.Id == own).Shared);
    }

    [Fact] // AC-3: a field private to another tenant is invisible, and so is its name
    public async Task AFieldPrivateToAnotherTenantIsNeitherListedNorInTheWay()
    {
        await DictionaryFixtures.AddFieldOfAnotherTenantAsync(App, "year", DictionaryFieldScope.Tenant);

        Assert.Empty(await DictionaryApi.ListAsync(Admin));
        await DictionaryApi.CreateOkAsync(Admin, "year");
        Assert.Single(await DictionaryApi.ListAsync(Admin));
    }

    [Fact] // D-10: a Global field is visible to every tenant, so it must not collide with a name a tenant already uses
    public async Task AGlobalFieldCannotTakeTheNameOfAnotherTenantsPrivateField()
    {
        await DictionaryFixtures.AddFieldOfAnotherTenantAsync(App, "year", DictionaryFieldScope.Tenant);

        using var response = await DictionaryApi.CreateAsync(Admin, "year", scope: "Global");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("knowledge_base.field_name_taken", await DictionaryApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ConcurrentCreationsOfTheSameNameLeaveExactlyOneField()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => DictionaryApi.CreateAsync(Admin, "year")));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Single(await DictionaryApi.ListAsync(Admin));
    }

    [Fact]
    public async Task ADescriptionUpToTheLimitIsAcceptedAndLongerIsNot()
    {
        var id = await DictionaryApi.CreateOkAsync(Admin, "year", description: new string('d', 500));
        using var tooLongOnCreate = await DictionaryApi.CreateAsync(Admin, "month", description: new string('d', 501));
        using var tooLongOnUpdate = await Admin.PutAsJsonAsync(DictionaryApi.Field(id), new { description = new string('d', 501) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, tooLongOnCreate.StatusCode);
        Assert.Equal("knowledge_base.invalid_field_description", await DictionaryApi.ProblemCodeAsync(tooLongOnCreate));
        Assert.Equal(HttpStatusCode.BadRequest, tooLongOnUpdate.StatusCode);
        Assert.Equal("knowledge_base.invalid_field_description", await DictionaryApi.ProblemCodeAsync(tooLongOnUpdate));
        Assert.Equal(500, Assert.Single(await DictionaryApi.ListAsync(Admin)).Description!.Length);
    }

    [Fact]
    public async Task TheCreatedResponseCarriesNoLocationToARouteThatCannotBeRead()
    {
        using var response = await DictionaryApi.CreateAsync(Admin, "year");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory] // AC-1: every type
    [InlineData("Text")]
    [InlineData("Number")]
    [InlineData("Boolean")]
    [InlineData("Date")]
    [InlineData("TextList")]
    public async Task EveryTypeOfTheSpecCanBeCreated(string type)
    {
        await DictionaryApi.CreateOkAsync(Admin, "field", type);

        Assert.Equal(type, Assert.Single(await DictionaryApi.ListAsync(Admin)).Type);
    }
}
