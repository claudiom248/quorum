using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases;

public sealed class KnowledgeBaseSharingTests(PostgresFixture postgres)
{
    private const string PrivateUrl = "http://qdrant.other-tenant.test:6334";
    private const string PrivateCollection = "other_tenant_secret_collection";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Inserts a knowledge base owned by a tenant that does not exist in the app: the API has no way to create a second tenant yet.</summary>
    private static async Task<Guid> AddKnowledgeBaseOfAnotherTenantAsync(
        QuorumApp app,
        string name,
        KnowledgeBaseScope scope,
        params Guid[] scopeTenantIds)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var knowledgeBase = KnowledgeBaseFixtures.Create(Guid.NewGuid(), name, PrivateUrl, PrivateCollection, scope, scopeTenantIds);
        db.KnowledgeBases.Add(knowledgeBase);
        await db.SaveChangesAsync(Ct);
        return knowledgeBase.Id;
    }

    [Fact] // AC-43 (KB half)
    public async Task AGlobalKnowledgeBaseOfAnotherTenantShowsNameDescriptionAndStatusOnly()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await AddKnowledgeBaseOfAnotherTenantAsync(app, "handbook", KnowledgeBaseScope.Global);

        var listed = Assert.Single(await KnowledgeBaseApi.ListAsync(admin));
        var detail = await KnowledgeBaseApi.GetAsync(admin, id);
        var raw = await KnowledgeBaseApi.GetRawAsync(admin, id);

        Assert.True(listed.Shared);
        Assert.Equal(("handbook", "Another tenant's handbook.", "Available"), (listed.Name, listed.Description, listed.Status));
        Assert.True(detail.Shared);
        Assert.Equal(("handbook", "Another tenant's handbook.", "Available"), (detail.Name, detail.Description, detail.Status));
        Assert.Null(detail.Connection);
        Assert.Null(detail.Mapping);
        Assert.Null(detail.Scope);
        Assert.Null(detail.Warning);
        foreach (var secret in new[] { PrivateUrl, PrivateCollection, "other-tenant", "s3cret", "apiKey", "connection" })
        {
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, await admin.GetStringAsync(KnowledgeBaseApi.All, Ct), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact] // AC-43
    public async Task ATenantScopeShowsTheKnowledgeBaseOnlyToTheTenantsItLists()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var tenantId = await TenantScope.TenantIdAsync(app);
        await AddKnowledgeBaseOfAnotherTenantAsync(app, "listed", KnowledgeBaseScope.Tenant, tenantId);
        await AddKnowledgeBaseOfAnotherTenantAsync(app, "elsewhere", KnowledgeBaseScope.Tenant, Guid.NewGuid());
        var hidden = await AddKnowledgeBaseOfAnotherTenantAsync(app, "private", KnowledgeBaseScope.Tenant);

        var names = (await KnowledgeBaseApi.ListAsync(admin)).Select(k => k.Name).ToArray();

        Assert.Equal(["listed"], names);
        using var read = await admin.GetAsync(KnowledgeBaseApi.One(hidden), Ct);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact] // AC-43
    public async Task AnAssistantScopeOfAnotherTenantIsNeverShown()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await AddKnowledgeBaseOfAnotherTenantAsync(app, "assistant-only", KnowledgeBaseScope.Assistant);

        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
        using var read = await admin.GetAsync(KnowledgeBaseApi.One(id), Ct);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact] // AC-43
    public async Task AKnowledgeBaseSharedByAnotherTenantIsReadOnlyHere()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var id = await AddKnowledgeBaseOfAnotherTenantAsync(app, "handbook", KnowledgeBaseScope.Global);

        using var update = await admin.PutAsJsonAsync(KnowledgeBaseApi.One(id), KnowledgeBaseApi.Body("http://localhost:6334", "docs", "stolen", mapping: KnowledgeBaseApi.TextOnly(), createFields: []), Ct);
        using var verify = await admin.PostAsync(KnowledgeBaseApi.Verify(id), content: null, Ct);
        using var delete = await admin.DeleteAsync(KnowledgeBaseApi.One(id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, verify.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("handbook", Assert.Single(await KnowledgeBaseApi.ListAsync(admin)).Name);
    }

    [Fact]
    public async Task TheSameNameCanBeUsedByTwoTenants()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await AddKnowledgeBaseOfAnotherTenantAsync(app, "policies", KnowledgeBaseScope.Tenant);

        using var response = await KnowledgeBaseApi.RegisterAsync(admin, "http://[::1]:1", "docs", mapping: KnowledgeBaseApi.TextOnly(), createFields: []);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
