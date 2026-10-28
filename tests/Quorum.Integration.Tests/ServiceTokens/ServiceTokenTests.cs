using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Tenancy.Persistence;

namespace Quorum.Integration.Tests.ServiceTokens;

public sealed class ServiceTokenTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri Tokens = new("/api/tenants/me/service-tokens", UriKind.Relative);

    [Fact] // AC-1
    public async Task GenerateReturnsPlaintextTokenAndId()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.PostAsync(Tokens, content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateResponse>(Ct);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.StartsWith("svc_", body.Token, StringComparison.Ordinal);
        Assert.True(body.Token.Length >= 40);
    }

    [Fact] // AC-2
    public async Task ListReturnsMetadataWithoutPlaintext()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await admin.PostAsync(Tokens, content: null, Ct);

        var list = await admin.GetFromJsonAsync<List<TokenInfo>>(Tokens, Ct);

        Assert.NotNull(list);
        var token = Assert.Single(list);
        Assert.NotEqual(Guid.Empty, token.Id);
    }

    [Fact] // AC-3
    public async Task RevokeRemovesTheTokenAndReturnsNoContent()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var created = await admin.PostAsJsonAsync(Tokens, new { }, Ct);
        var body = await created.Content.ReadFromJsonAsync<CreateResponse>(Ct);

        using var delete = await admin.DeleteAsync(new Uri($"/api/tenants/me/service-tokens/{body!.Id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var exists = await db.ServiceTokens.IgnoreQueryFilters().AnyAsync(t => t.Id == body.Id, Ct);
        Assert.False(exists);
    }

    [Fact] // AC-3
    public async Task RevokeUnknownTokenReturnsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var delete = await admin.DeleteAsync(new Uri($"/api/tenants/me/service-tokens/{Guid.NewGuid()}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    private sealed record CreateResponse(Guid Id, string Token, DateTimeOffset CreatedAt);

    private sealed record TokenInfo(Guid Id, DateTimeOffset CreatedAt);
}
