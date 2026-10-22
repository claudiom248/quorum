using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>The <em>lookups</em> capability of the Qdrant kind: what it answers and what it says when it cannot (spec 007 D-15).</summary>
public sealed class QdrantLookupsCapabilityTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(QuorumApp App, ILookupsCapability Capability, QdrantConnection Connection)> StartAsync(string collection)
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var adapter = app.Services.GetRequiredService<StoreAdapters>().For(KnowledgeBaseKind.Qdrant);
        return (app, adapter.Capability<ILookupsCapability>()!, new QdrantConnection(await qdrant.GetUrlAsync(), collection, ApiKey: null, VectorName: null));
    }

    private async Task<string> IndexedAsync() => await qdrant.SeedSharedAsync(SampleCollections.CreateLookups);

    [Theory]
    [InlineData("1.12.0", true)]
    [InlineData("1.13.2", true)]
    [InlineData("1.11.5", false)]
    [InlineData("1.10.0", false)]
    [InlineData("2.0.0", true)]
    public void LookupsNeedQdrantOnePointTwelveOrLater(string version, bool supported) => // AC-8
        Assert.Equal(supported, QdrantConnector.SupportsLookups(Version.Parse(version)));

    [Fact]
    public async Task OnlyQdrantDeclaresTheCapabilityAndTheFakeKindDoesNot()
    {
        var (app, capability, _) = await StartAsync(await IndexedAsync());
        await using var _ = app;

        Assert.NotNull(capability);
        Assert.Null(new Storage.FakeStoreAdapter([]).Capability<ILookupsCapability>());
    }

    [Fact]
    public async Task ACollectionWithEveryIndexTheLookupReadsCanBeComputedAndOneWithoutNamesTheFieldsThatLack()
    {
        var indexed = await IndexedAsync();
        var unindexed = QdrantFixture.NewPrefix() + SampleCollections.LookupsUnindexed;
        await qdrant.SeedAsync([SampleCollections.CreateLookupsUnindexed(unindexed)]);
        var (app, capability, connection) = await StartAsync(indexed);
        await using var _ = app;

        var ready = await capability.CheckAsync(connection, ["supplier", "is_first_chunk"], Ct);
        var missing = await capability.CheckAsync(connection with { Collection = unindexed }, ["supplier", "is_first_chunk"], Ct);
        var absent = await capability.CheckAsync(connection with { Collection = "no_such_collection" }, ["supplier"], Ct);

        Assert.Null(ready);
        Assert.Contains("'is_first_chunk', 'supplier'", missing, StringComparison.Ordinal);
        Assert.Contains("does not exist", absent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDistinctValuesUnderAConditionAreCountedExactlyAndCutAtTheCeiling()
    {
        var (app, capability, connection) = await StartAsync(await IndexedAsync());
        await using var _ = app;

        var all = await capability.DistinctValuesAsync(connection, new DistinctValuesQuery("supplier", [], null, 10, 1000), Ct);
        var firstOnly = await capability.DistinctValuesAsync(connection, new DistinctValuesQuery("supplier", [new MatchBooleanCondition("is_first_chunk", true)], null, 10, 1000), Ct);
        var cut = await capability.DistinctValuesAsync(connection, new DistinctValuesQuery("supplier", [], null, 2, 1000), Ct);

        Assert.Equal([("Acme", 7), ("Globex", 3), ("Initech", 1)], all.Values.Select(v => (v.Value, v.Count)));
        Assert.False(all.Truncated);
        Assert.Equal([("Acme", 3), ("Globex", 2), ("Initech", 1)], firstOnly.Values.Select(v => (v.Value, v.Count)));
        Assert.Equal(["Acme", "Globex"], cut.Values.Select(v => v.Value));
        Assert.True(cut.Truncated);
    }

    [Fact] // AC-4
    public async Task TheDistinctDocumentsAreCountedByReadingTheRecordsAndTheCapStopsTheScan()
    {
        var (app, capability, connection) = await StartAsync(await IndexedAsync());
        await using var _ = app;

        var counted = await capability.DistinctValuesAsync(connection, new DistinctValuesQuery("audience", [], "document_id", 10, 1000), Ct);
        var capped = await capability.DistinctValuesAsync(connection, new DistinctValuesQuery("audience", [], "document_id", 10, 4), Ct);

        Assert.Equal([("managers", 3), ("all", 2), ("it", 2)], counted.Values.Select(v => (v.Value, v.Count)));
        Assert.False(counted.Truncated);
        Assert.True(capped.Truncated);
        Assert.NotEmpty(capped.Values);
    }

    [Fact]
    public async Task ARecordCountIsExactUnderEveryCondition()
    {
        var (app, capability, connection) = await StartAsync(await IndexedAsync());
        await using var _ = app;

        Assert.Equal(11, await capability.CountAsync(connection, [], Ct));
        Assert.Equal(6, await capability.CountAsync(connection, [new MatchBooleanCondition("is_first_chunk", true)], Ct));
        Assert.Equal(6, await capability.CountAsync(connection, [new NumberRangeCondition("chunk_index", 0, 0)], Ct));
        Assert.Equal(7, await capability.CountAsync(connection, [new MatchAnyCondition("supplier", ["Acme"])], Ct));
        Assert.Equal(3, await capability.CountAsync(connection, [new MatchAnyCondition("supplier", ["Acme"]), new MatchBooleanCondition("is_first_chunk", true)], Ct));
    }

    [Fact]
    public async Task AScanPagesThroughTheRecordsWithTheCursorAndReadsOnlyTheRequestedPaths()
    {
        var (app, capability, connection) = await StartAsync(await IndexedAsync());
        await using var _ = app;

        var rows = new List<JsonObject>();
        var pages = 0;
        string? cursor = null;
        do
        {
            var page = await capability.ScanAsync(connection, new RecordQuery([new MatchAnyCondition("supplier", ["Acme"])], ["document_id", "chunk_index"], 3, cursor), Ct);
            rows.AddRange(page.Rows);
            cursor = page.Cursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(7, rows.Count);
        Assert.All(rows, row => Assert.Equal(["chunk_index", "document_id"], row.Select(p => p.Key).Order(StringComparer.Ordinal)));
        Assert.Equal(["c1", "c2", "c5"], rows.Select(r => r["document_id"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACursorOfAnotherSourceIsRefusedNotFollowed()
    {
        var (app, capability, connection) = await StartAsync(await IndexedAsync());
        await using var _ = app;

        var refused = await Assert.ThrowsAsync<StoreException>(() => capability.ScanAsync(connection, new RecordQuery([], ["document_id"], 3, "not-a-cursor"), Ct));

        Assert.Equal(StoreFailure.Rejected, refused.Failure);
    }
}
