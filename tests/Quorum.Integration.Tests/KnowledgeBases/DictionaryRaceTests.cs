using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>
/// Saving a knowledge base and deleting a dictionary field both take the dictionary lock. The tests hold the lock themselves to
/// put the request in the queue, change the world as the request that "came first" would have, and release it.
/// </summary>
public sealed class DictionaryRaceTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Waits until a request is queued behind an advisory lock of this database.</summary>
    private static async Task WaitForAWaiterAsync(QuorumApp app)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await ToolFixtures.ScalarAsync<long>(
            app,
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())") == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("No request waited for the dictionary lock.");
            }

            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task AFieldDeletedWhileTheKnowledgeBaseWaitsForTheLockIsRefused()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        KnowledgeBaseApi.ServeEmbeddings(app);
        var collection = QdrantFixture.NewPrefix() + SampleCollections.Flat;
        await qdrant.SeedAsync([SampleCollections.CreateFlat(collection)]);
        var url = (await qdrant.GetUrlAsync()).ToString();
        var yearId = await DictionaryApi.CreateOkAsync(admin, "year", "Number");
        var mapping = new { roles = new { text = "text" }, fields = new[] { new { path = "year", dictionaryFieldId = yearId } } };

        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        await using var held = await db.Database.BeginTransactionAsync(Ct);
        await DictionaryLock.AcquireAsync(db, Ct);

        // The registration resolves the field and probes the store, then queues behind the lock.
        var registration = KnowledgeBaseApi.RegisterAsync(admin, url, collection, mapping: mapping, createFields: []);
        await WaitForAWaiterAsync(app);

        // The deletion that holds the lock goes first.
        db.DictionaryFields.Remove(await db.DictionaryFields.SingleAsync(f => f.Id == yearId, Ct));
        await db.SaveChangesAsync(Ct);
        await held.CommitAsync(Ct);

        using var response = await registration;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("knowledge_base.field_removed", await KnowledgeBaseApi.ProblemCodeAsync(response));
        Assert.Empty(await KnowledgeBaseApi.ListAsync(admin));
    }

    [Fact]
    public async Task AKnowledgeBaseSavedWhileTheDeletionWaitsForTheLockKeepsTheField()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        var yearId = await DictionaryApi.CreateOkAsync(admin, "year", "Number");
        var tenantId = await TenantScope.TenantIdAsync(app);

        await using var scope = TenantScope.Open(app, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        await using var held = await db.Database.BeginTransactionAsync(Ct);
        await DictionaryLock.AcquireAsync(db, Ct);

        // The deletion finds the field unused so far, then queues behind the lock.
        var deletion = admin.DeleteAsync(DictionaryApi.Field(yearId), Ct);
        await WaitForAWaiterAsync(app);

        // The knowledge base that holds the lock is saved first.
        db.KnowledgeBases.Add(KnowledgeBaseFixtures.Create(tenantId, "policies", "http://localhost:6334", "docs", KnowledgeBaseScope.Tenant, [], [yearId]));
        await db.SaveChangesAsync(Ct);
        await held.CommitAsync(Ct);

        using var response = await deletion;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("knowledge_base.field_in_use", await DictionaryApi.ProblemCodeAsync(response));
        Assert.Equal(1, Assert.Single(await DictionaryApi.ListAsync(admin)).UsedByCount);
    }
}
