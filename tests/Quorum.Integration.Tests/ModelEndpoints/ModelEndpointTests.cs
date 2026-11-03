using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.ModelEndpoints;

public sealed class ModelEndpointTests(PostgresFixture postgres)
{
    private static readonly Uri Endpoints = new("/api/model-endpoints", UriKind.Relative);
    private static readonly Uri TestConnection = new("/api/model-endpoints/test", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string name, string? apiKey = null, string baseUrl = QuorumApp.ModelBaseUrl, int budget = 4096) =>
        new { name, baseUrl, modelName = "llama3.2", apiKey, contextTokenBudget = budget };

    [Fact]
    public async Task ListsTheEndpointCreatedByTheWizardNamedAfterItsModel()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync(contextTokenBudget: 8192);

        var endpoints = await client.GetFromJsonAsync<List<EndpointBody>>(Endpoints, Ct);

        var endpoint = Assert.Single(endpoints!);
        Assert.Equal("llama3.2", endpoint.Name);
        Assert.Equal(QuorumApp.ModelBaseUrl, endpoint.BaseUrl);
        Assert.Equal(8192, endpoint.ContextTokenBudget);
    }

    [Fact]
    public async Task CreatesAnEndpointAndNeverReturnsItsKey()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var created = await client.PostAsJsonAsync(Endpoints, Body("cloud", apiKey: "sk-secret"), Ct);
        var raw = await client.GetStringAsync(Endpoints, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = created.Headers.Location!;
        var fetched = await client.GetFromJsonAsync<EndpointBody>(location, Ct);
        Assert.Equal("cloud", fetched!.Name);
        Assert.DoesNotContain("sk-secret", raw, StringComparison.Ordinal);
        var list = JsonSerializer.Deserialize<List<EndpointBody>>(raw, JsonSerializerOptions.Web)!;
        Assert.True(list.Single(e => e.Name == "cloud").HasApiKey);
        Assert.True(list.Single(e => e.Name == "cloud").SupportsTools);
    }

    [Fact]
    public async Task ANameIsUniquePerTenant()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        using var _ = await client.PostAsJsonAsync(Endpoints, Body("cloud"), Ct);

        using var duplicate = await client.PostAsJsonAsync(Endpoints, Body(" cloud "), Ct);
        using var other = await client.PostAsJsonAsync(Endpoints, Body("cloud-2"), Ct);
        var otherId = (await other.Content.ReadFromJsonAsync<EndpointBody>(Ct))!.Id;
        using var rename = await client.PutAsJsonAsync(new Uri($"/api/model-endpoints/{otherId}", UriKind.Relative), Body("cloud"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
    }

    [Fact]
    public async Task InvalidEndpointsAreRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var response = await client.PostAsJsonAsync(Endpoints, new { name = " ", baseUrl = "nope", modelName = "", contextTokenBudget = 10 }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        Assert.Equal(["baseUrl", "contextTokenBudget", "modelName", "name"], errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task UpdateKeepsTheKeyWhenOmittedAndRemovesItWhenEmpty()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        using var created = await client.PostAsJsonAsync(Endpoints, Body("cloud", apiKey: "sk-secret"), Ct);
        var uri = new Uri($"/api/model-endpoints/{(await created.Content.ReadFromJsonAsync<EndpointBody>(Ct))!.Id}", UriKind.Relative);

        using var omitted = await client.PutAsJsonAsync(uri, Body("cloud-renamed", budget: 8192), Ct);
        var afterOmitted = (await client.GetFromJsonAsync<List<EndpointBody>>(Endpoints, Ct))!.Single(e => e.Name == "cloud-renamed");
        using var empty = await client.PutAsJsonAsync(uri, Body("cloud-renamed", apiKey: ""), Ct);
        var afterEmpty = (await client.GetFromJsonAsync<List<EndpointBody>>(Endpoints, Ct))!.Single(e => e.Name == "cloud-renamed");

        Assert.Equal(HttpStatusCode.NoContent, omitted.StatusCode);
        Assert.True(afterOmitted.HasApiKey);
        Assert.Equal(8192, afterOmitted.ContextTokenBudget);
        Assert.False(afterEmpty.HasApiKey);
    }

    [Fact]
    public async Task UpdatingOrDeletingAnUnknownEndpointIsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var uri = new Uri($"/api/model-endpoints/{Guid.NewGuid()}", UriKind.Relative);

        using var update = await client.PutAsJsonAsync(uri, Body("x"), Ct);
        using var delete = await client.DeleteAsync(uri, Ct);

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact] // AC-8
    public async Task AnEndpointInUseIsNotDeletedAndListsWhatUsesIt()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();
        var inUse = (await client.GetFromJsonAsync<List<EndpointBody>>(Endpoints, Ct))!.Single();
        using var spare = await client.PostAsJsonAsync(Endpoints, Body("spare"), Ct);
        var spareId = (await spare.Content.ReadFromJsonAsync<EndpointBody>(Ct))!.Id;

        using var refused = await client.DeleteAsync(new Uri($"/api/model-endpoints/{inUse.Id}", UriKind.Relative), Ct);
        using var deleted = await client.DeleteAsync(new Uri($"/api/model-endpoints/{spareId}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var reference = Assert.Single(problem.GetProperty("references").EnumerateArray());
        Assert.Equal("assistant", reference.GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<List<EndpointBody>>(Endpoints, Ct))!);
    }

    [Fact]
    public async Task TestsAnEndpointWithoutSavingIt()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await app.SetUpAsync();

        using var reachable = await client.PostAsJsonAsync(TestConnection, new { baseUrl = QuorumApp.ModelBaseUrl, apiKey = "sk-try" }, Ct);
        var authorization = app.ModelServer.LastAuthorization;
        using var unreachable = await client.PostAsJsonAsync(TestConnection, new { baseUrl = "http://nowhere.test/v1" }, Ct);
        using var invalid = await client.PostAsJsonAsync(TestConnection, new { baseUrl = "nope" }, Ct);

        Assert.Equal(HttpStatusCode.OK, reachable.StatusCode);
        Assert.Equal("Bearer sk-try", authorization);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unreachable.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<List<EndpointBody>>(Endpoints, Ct))!);
    }

    [Fact] // AC-24 (endpoint writes)
    public async Task WithoutTheAgentsManagePermissionEveryCallIsForbidden()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        var id = (await admin.GetFromJsonAsync<List<EndpointBody>>(Endpoints, Ct))!.Single().Id;
        var item = new Uri($"/api/model-endpoints/{id}", UriKind.Relative);

        var responses = new[]
        {
            await member.GetAsync(Endpoints, Ct),
            await member.PostAsJsonAsync(Endpoints, Body("x"), Ct),
            await member.PostAsJsonAsync(TestConnection, new { baseUrl = QuorumApp.ModelBaseUrl }, Ct),
            await member.PutAsJsonAsync(item, Body("x"), Ct),
            await member.DeleteAsync(item, Ct),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    private sealed record EndpointBody(Guid Id, string Name, string BaseUrl, string ModelName, bool HasApiKey, int ContextTokenBudget, bool SupportsTools);
}
