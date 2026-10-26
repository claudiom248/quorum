using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>The cache in front of the tables and the store that writes generations (spec 007 D-10, D-11).</summary>
public sealed class LookupCacheTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<(string Value, int Count)> Suppliers { get; } =
    [
        ("Acme", 90), ("Globex", 90), ("Initech", 70), ("umbrella", 40), ("Éclair", 40), ("Hooli", 10), ("Soylent", 5), ("50% off", 3), ("a_b", 3), ("Zeta", 1),
    ];

    private async Task<(QuorumApp App, Guid TenantId)> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        (await app.SetUpAsync()).Dispose();
        return (app, await TenantScope.TenantIdAsync(app));
    }

    private static async Task<(Lookup Lookup, LookupSlice Slice)> LoadAsync(KnowledgeBasesDbContext db, Guid lookupId, Guid sliceId) =>
        (await db.Lookups.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == lookupId, Ct),
            await db.LookupSlices.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == sliceId, Ct));

    private static async Task<List<(string Value, int? Count)>> WalkAsync(Func<string?, Task<LookupPage>> read)
    {
        var all = new List<(string, int?)>();
        string? cursor = null;
        do
        {
            var page = await read(cursor);
            all.AddRange(page.Values.Select(v => (v.Value, v.Count)));
            cursor = page.Cursor;
        }
        while (cursor is not null);

        return all;
    }

    [Fact]
    public async Task TheCacheAndTheTablesPageEveryOrderSearchAndCursorIdentically()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedComputedAsync(app, tenantId, Suppliers);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<LookupCache>();
        var (lookup, slice) = await LoadAsync(db, seeded.LookupId, seeded.SliceId);

        foreach (var sort in new[] { LookupSort.Count, LookupSort.Name })
        {
            foreach (var search in new string?[] { null, "a", "50%", "_", "ÉCL", "nothing" })
            {
                var fromTables = await WalkAsync(async cursor => (await LookupReader.ReadAsync(db, slice, new LookupPageRequest(search, sort, 3, cursor), Ct)).Value);
                var fromCache = await WalkAsync(async cursor => (await cache.ReadAsync(db, lookup, slice, new LookupPageRequest(search, sort, 3, cursor), Ct)).Value);

                Assert.Equal(fromTables, fromCache);
            }
        }

        // A cursor issued by one is valid for the other, so a reader can move between them mid-list.
        var first = (await cache.ReadAsync(db, lookup, slice, new LookupPageRequest(null, LookupSort.Name, 4, null), Ct)).Value;
        var next = (await LookupReader.ReadAsync(db, slice, new LookupPageRequest(null, LookupSort.Name, 4, first.Cursor), Ct)).Value;
        var viaCache = (await cache.ReadAsync(db, lookup, slice, new LookupPageRequest(null, LookupSort.Name, 4, first.Cursor), Ct)).Value;
        Assert.Equal(next.Values.Select(v => v.Value), viaCache.Values.Select(v => v.Value));
    }

    [Fact]
    public async Task ACursorOfAnOlderGenerationRestartsTheListAndAMalformedOneIsRefusedWhicheverPathAnswers()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedComputedAsync(app, tenantId, Suppliers);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<LookupCache>();
        var (lookup, slice) = await LoadAsync(db, seeded.LookupId, seeded.SliceId);
        var old = (await cache.ReadAsync(db, lookup, slice, new LookupPageRequest(null, LookupSort.Count, 2, null), Ct)).Value;
        var tracked = await db.LookupSlices.IgnoreQueryFilters().SingleAsync(s => s.Id == seeded.SliceId, Ct);
        await LookupApi.AddGenerationAsync(db, tenantId, tracked, 2, [("only", 1)]);
        await app.Services.GetRequiredService<LookupCache>().EvictSliceAsync(seeded.SliceId, Ct);
        var (_, current) = await LoadAsync(db, seeded.LookupId, seeded.SliceId);

        var restarted = (await cache.ReadAsync(db, lookup, current, new LookupPageRequest(null, LookupSort.Count, 2, old.Cursor), Ct)).Value;
        var malformed = await cache.ReadAsync(db, lookup, current, new LookupPageRequest(null, LookupSort.Count, 2, "not-a-cursor"), Ct);

        Assert.True(restarted.CursorRestarted);
        Assert.Equal(["only"], restarted.Values.Select(v => v.Value));
        Assert.False(malformed.IsSuccess);
        Assert.Equal("lookup.invalid_cursor", malformed.Error.Code);
    }

    [Fact] // AC-18
    public async Task AFailingCacheNeverFailsARead()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedComputedAsync(app, tenantId, Suppliers);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var (lookup, slice) = await LoadAsync(db, seeded.LookupId, seeded.SliceId);
        var broken = new LookupCache(new ThrowingHybridCache(), app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LookupCache>.Instance);

        var page = await broken.ReadAsync(db, lookup, slice, new LookupPageRequest(null, LookupSort.Count, 3, null), Ct);
        await broken.EvictSliceAsync(slice.Id, Ct);
        await broken.EvictKnowledgeBaseAsync(lookup.KnowledgeBaseId!.Value, Ct);

        Assert.Equal(["Acme", "Globex", "Initech"], page.Value.Values.Select(v => v.Value));
        Assert.NotNull(page.Value.Cursor);
    }

    [Fact] // AC-18
    public async Task AnEmptyCacheIsFilledFromTheTablesOnTheFirstReadAndAnsweredFromMemoryAfter()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedComputedAsync(app, tenantId, Suppliers);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<LookupCache>();
        var (lookup, slice) = await LoadAsync(db, seeded.LookupId, seeded.SliceId);
        var request = new LookupPageRequest(null, LookupSort.Count, 3, null);

        var cold = await cache.ReadAsync(db, lookup, slice, request, Ct);
        await db.LookupValues.IgnoreQueryFilters().Where(v => v.SliceId == slice.Id).ExecuteDeleteAsync(Ct);
        var warm = await cache.ReadAsync(db, lookup, slice, request, Ct);
        await cache.EvictSliceAsync(slice.Id, Ct);
        var evicted = await cache.ReadAsync(db, lookup, slice, request, Ct);

        Assert.Equal(["Acme", "Globex", "Initech"], cold.Value.Values.Select(v => v.Value));
        Assert.Equal(cold.Value.Values.Select(v => v.Value), warm.Value.Values.Select(v => v.Value));
        Assert.Empty(evicted.Value.Values);
    }

    [Fact]
    public async Task EvictingAKnowledgeBaseDropsItsListsAndNoDistributedCacheIsInvolved()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedComputedAsync(app, tenantId, Suppliers);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<LookupCache>();
        var (lookup, slice) = await LoadAsync(db, seeded.LookupId, seeded.SliceId);
        var request = new LookupPageRequest(null, LookupSort.Count, 3, null);
        await cache.ReadAsync(db, lookup, slice, request, Ct);
        await db.LookupValues.IgnoreQueryFilters().Where(v => v.SliceId == slice.Id).ExecuteDeleteAsync(Ct);

        await cache.EvictKnowledgeBaseAsync(lookup.KnowledgeBaseId!.Value, Ct);
        var after = await cache.ReadAsync(db, lookup, slice, request, Ct);

        Assert.Empty(after.Value.Values);
        Assert.Null(scope.ServiceProvider.GetService<IDistributedCache>());
    }

    [Fact] // AC-13
    public async Task AGenerationThatIsWrittenButNotSwappedIsInvisibleAndTheSwapReplacesTheWholeList()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedComputedAsync(app, tenantId, [("old", 2)]);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<LookupStore>();
        var slice = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == seeded.SliceId, Ct);

        await store.WriteGenerationAsync(slice, 2, [new ComputedValue("new-1", 5), new ComputedValue("new-2", 4)], Ct);
        var hidden = await LookupReader.ReadAsync(db, slice, new LookupPageRequest(null, LookupSort.Count, 10, null), Ct);
        await store.SwapAsync(slice.Id, 2, 2, 0, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), Ct);
        var swapped = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == seeded.SliceId, Ct);
        var visible = await LookupReader.ReadAsync(db, swapped, new LookupPageRequest(null, LookupSort.Count, 10, null), Ct);

        Assert.Equal(["old"], hidden.Value.Values.Select(v => v.Value));
        Assert.Equal(["new-1", "new-2"], visible.Value.Values.Select(v => v.Value));
        Assert.Equal([2], await db.LookupValues.IgnoreQueryFilters().Where(v => v.SliceId == slice.Id).Select(v => v.Generation).Distinct().ToListAsync(Ct));
    }

    [Fact]
    public async Task AGenerationAtTheMaximumIsWrittenInBatchesAndSwappedInOneTransaction()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedComputedAsync(app, tenantId, [("old", 2)]);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<LookupStore>();
        var slice = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == seeded.SliceId, Ct);
        var values = Enumerable.Range(0, LookupOptions.MaxValuesPerLookupLimit).Select(i => new ComputedValue($"value-{i:D6}", LookupOptions.MaxValuesPerLookupLimit - i)).ToList();

        await store.WriteGenerationAsync(slice, 2, values, Ct);
        await store.SwapAsync(slice.Id, 2, values.Count, 0, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), Ct);

        Assert.Equal(LookupOptions.MaxValuesPerLookupLimit, await db.LookupValues.IgnoreQueryFilters().CountAsync(v => v.SliceId == slice.Id, Ct));
        Assert.Equal(0, await db.LookupValues.IgnoreQueryFilters().CountAsync(v => v.SliceId == slice.Id && v.Generation == 1, Ct));
        Assert.Empty(db.ChangeTracker.Entries<LookupValue>());
    }

    [Fact]
    public async Task DiscardingTheUnpublishedKeepsTheCurrentGenerationAndNothingElse()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedComputedAsync(app, tenantId, [("current", 2)]);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<LookupStore>();
        var slice = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == seeded.SliceId, Ct);
        await store.WriteGenerationAsync(slice, 2, [new ComputedValue("half", 1)], Ct);
        await store.WriteGenerationAsync(slice, 3, [new ComputedValue("half", 1)], Ct);

        var deleted = await store.DiscardUnpublishedAsync(slice.Id, Ct);

        Assert.Equal(2, deleted);
        Assert.Equal(["current"], await db.LookupValues.IgnoreQueryFilters().Where(v => v.SliceId == slice.Id).Select(v => v.Value).ToListAsync(Ct));
        Assert.Equal(1, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM knowledge_base.lookup_values"));
    }

    private sealed class ThrowingHybridCache : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("The cache is down.");

        public override ValueTask SetAsync<T>(string key, T value, HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The cache is down.");

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The cache is down.");

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The cache is down.");
    }
}
