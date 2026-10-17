using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBase.Domain;

namespace Quorum.Integration.Tests.KnowledgeBase;

public sealed class DictionaryFieldTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-1
    public async Task AFieldWithAValidNameAndTypeIsListedAsUnused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await DictionaryApi.CreateAsync(admin, "year", "Number", "Publication year", "Tenant");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DictionaryApi.CreatedBody>(Ct);
        var field = Assert.Single(await DictionaryApi.ListAsync(admin));
        Assert.Equal(created!.Id, field.Id);
        Assert.Equal("year", field.Name);
        Assert.Equal("Number", field.Type);
        Assert.Equal("Publication year", field.Description);
        Assert.Equal("Tenant", field.Scope);
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
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await DictionaryApi.CreateAsync(admin, name, "Number");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.reserved_field_name", await DictionaryApi.ProblemCodeAsync(response));
        Assert.Empty(await DictionaryApi.ListAsync(admin));
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
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await DictionaryApi.CreateAsync(admin, name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("knowledge_base.invalid_field_name", await DictionaryApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ANameUpToFortyCharactersIsAcceptedAndLongerIsNot()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        await DictionaryApi.CreateOkAsync(admin, "a" + new string('b', 39));
        using var tooLong = await DictionaryApi.CreateAsync(admin, "a" + new string('b', 40));

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
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await DictionaryApi.CreateAsync(admin, "year", type, scope: scope);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await DictionaryApi.ListAsync(admin));
    }

    [Fact] // AC-3
    public async Task AFieldWithTheNameOfAVisibleFieldIsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await DictionaryApi.CreateOkAsync(admin, "year", "Number");

        using var response = await DictionaryApi.CreateAsync(admin, "year", "Text");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("knowledge_base.field_name_taken", await DictionaryApi.ProblemCodeAsync(response));
        Assert.Equal("Number", Assert.Single(await DictionaryApi.ListAsync(admin)).Type);
    }

    [Theory] // AC-3, D-10: a Global field of another tenant is visible, so its name is taken
    [InlineData("Tenant")]
    [InlineData("Global")]
    public async Task AGlobalFieldOfAnotherTenantIsVisibleAndItsNameIsTaken(string scope)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var shared = await DictionaryFixtures.AddFieldOfAnotherTenantAsync(app, "year", DictionaryFieldScope.Global);

        using var response = await DictionaryApi.CreateAsync(admin, "year", scope: scope);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("knowledge_base.field_name_taken", await DictionaryApi.ProblemCodeAsync(response));
        var listed = Assert.Single(await DictionaryApi.ListAsync(admin));
        Assert.Equal(shared, listed.Id);
        Assert.Equal("Global", listed.Scope);
    }

    [Fact] // AC-3: a field private to another tenant is invisible, and so is its name
    public async Task AFieldPrivateToAnotherTenantIsNeitherListedNorInTheWay()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await DictionaryFixtures.AddFieldOfAnotherTenantAsync(app, "year", DictionaryFieldScope.Tenant);

        Assert.Empty(await DictionaryApi.ListAsync(admin));
        await DictionaryApi.CreateOkAsync(admin, "year");
        Assert.Single(await DictionaryApi.ListAsync(admin));
    }

    [Fact] // D-10: a Global field is visible to every tenant, so it must not collide with a name a tenant already uses
    public async Task AGlobalFieldCannotTakeTheNameOfAnotherTenantsPrivateField()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await DictionaryFixtures.AddFieldOfAnotherTenantAsync(app, "year", DictionaryFieldScope.Tenant);

        using var response = await DictionaryApi.CreateAsync(admin, "year", scope: "Global");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("knowledge_base.field_name_taken", await DictionaryApi.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ConcurrentCreationsOfTheSameNameLeaveExactlyOneField()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => DictionaryApi.CreateAsync(admin, "year")));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Single(await DictionaryApi.ListAsync(admin));
    }

    [Fact]
    public async Task ADescriptionUpToTheLimitIsAcceptedAndLongerIsNot()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var id = await DictionaryApi.CreateOkAsync(admin, "year", description: new string('d', 500));
        using var tooLongOnCreate = await DictionaryApi.CreateAsync(admin, "month", description: new string('d', 501));
        using var tooLongOnUpdate = await admin.PutAsJsonAsync(DictionaryApi.Field(id), new { description = new string('d', 501) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, tooLongOnCreate.StatusCode);
        Assert.Equal("knowledge_base.invalid_field_description", await DictionaryApi.ProblemCodeAsync(tooLongOnCreate));
        Assert.Equal(HttpStatusCode.BadRequest, tooLongOnUpdate.StatusCode);
        Assert.Equal("knowledge_base.invalid_field_description", await DictionaryApi.ProblemCodeAsync(tooLongOnUpdate));
        Assert.Equal(500, Assert.Single(await DictionaryApi.ListAsync(admin)).Description!.Length);
    }

    [Fact]
    public async Task TheCreatedResponseCarriesNoLocationToARouteThatCannotBeRead()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await DictionaryApi.CreateAsync(admin, "year");

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
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        await DictionaryApi.CreateOkAsync(admin, "field", type);

        Assert.Equal(type, Assert.Single(await DictionaryApi.ListAsync(admin)).Type);
    }
}
