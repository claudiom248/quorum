using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBase.Domain;

namespace Quorum.Integration.Tests.KnowledgeBase;

public sealed class DictionaryFieldChangeTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-4: the description is editable
    public async Task TheDescriptionCanBeChanged()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await DictionaryApi.CreateOkAsync(admin, "year", "Number", "Old");

        using var response = await admin.PutAsJsonAsync(DictionaryApi.Field(id), new { description = "New" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("New", Assert.Single(await DictionaryApi.ListAsync(admin)).Description);
    }

    [Fact] // AC-4: name and type are fixed
    public async Task TheNameAndTheTypeCannotBeChanged()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await DictionaryApi.CreateOkAsync(admin, "year", "Number", "Old");

        using var response = await admin.PutAsJsonAsync(DictionaryApi.Field(id), new { name = "month", type = "Text", scope = "Global", description = "New" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var field = Assert.Single(await DictionaryApi.ListAsync(admin));
        Assert.Equal("year", field.Name);
        Assert.Equal("Number", field.Type);
        Assert.Equal("Tenant", field.Scope);
        Assert.Equal("New", field.Description);
    }

    [Fact]
    public async Task ADescriptionCanBeCleared()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await DictionaryApi.CreateOkAsync(admin, "year", description: "Old");

        using var response = await admin.PutAsJsonAsync(DictionaryApi.Field(id), new { description = (string?)null }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(Assert.Single(await DictionaryApi.ListAsync(admin)).Description);
    }

    [Fact]
    public async Task AFieldCanBeDeleted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await DictionaryApi.CreateOkAsync(admin, "year");
        await DictionaryApi.CreateOkAsync(admin, "month");

        using var response = await admin.DeleteAsync(DictionaryApi.Field(id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("month", Assert.Single(await DictionaryApi.ListAsync(admin)).Name);
    }

    [Fact]
    public async Task ADeletedNameCanBeUsedAgain()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await DictionaryApi.CreateOkAsync(admin, "year", "Number");
        using var deleted = await admin.DeleteAsync(DictionaryApi.Field(id), Ct);

        await DictionaryApi.CreateOkAsync(admin, "year", "Text");

        Assert.Equal("Text", Assert.Single(await DictionaryApi.ListAsync(admin)).Type);
    }

    [Fact]
    public async Task AnUnknownFieldIsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var unknown = DictionaryApi.Field(Guid.NewGuid());

        using var update = await admin.PutAsJsonAsync(unknown, new { description = "New" }, Ct);
        using var delete = await admin.DeleteAsync(unknown, Ct);

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact] // AC-43 (field half, writes): a Global field of another tenant is visible but never writable
    public async Task AFieldSharedByAnotherTenantCannotBeChangedOrDeleted()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var shared = await DictionaryFixtures.AddFieldOfAnotherTenantAsync(app, "year", DictionaryFieldScope.Global);

        using var update = await admin.PutAsJsonAsync(DictionaryApi.Field(shared), new { description = "Mine now" }, Ct);
        using var delete = await admin.DeleteAsync(DictionaryApi.Field(shared), Ct);

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("Another tenant's field", Assert.Single(await DictionaryApi.ListAsync(admin)).Description);
    }
}
