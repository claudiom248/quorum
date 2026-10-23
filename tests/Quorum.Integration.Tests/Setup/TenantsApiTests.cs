using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Setup;

public sealed class TenantsApiTests(PostgresFixture postgres)
{
    private static readonly Uri Tenants = new("/api/tenants", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-6
    public async Task AdminListsTheTenantsAndSeesWhichOneIsTheirs()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var tenants = await admin.GetFromJsonAsync<TenantBody[]>(Tenants, Ct);

        var tenant = Assert.Single(tenants!);
        Assert.Equal("Default", tenant.Name);
        Assert.True(tenant.Current);
    }

    [Fact] // AC-6
    public async Task AnotherTenantIsListedButNotMarkedAsCurrent()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Quorum.Modules.Tenancy.Persistence.TenancyDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO tenancy.tenants (\"Id\", \"Name\", \"CreatedAt\") VALUES ({0}, 'Beta', now())", [Guid.CreateVersion7()], Ct);
        }

        var tenants = await admin.GetFromJsonAsync<TenantBody[]>(Tenants, Ct);

        Assert.Equal(["Beta", "Default"], tenants!.Select(t => t.Name).Order());
        Assert.Equal("Default", Assert.Single(tenants!, t => t.Current).Name);
    }

    [Fact] // AC-6
    public async Task NonAdminGetsForbidden()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();

        using var response = await member.GetAsync(Tenants, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact] // AC-6
    public async Task AnonymousGetsUnauthorized()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateUiClient();

        using var response = await client.GetAsync(Tenants, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record TenantBody(Guid Id, string Name, bool Current);
}
