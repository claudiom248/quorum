using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.Tools;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>The tables of spec 007 and the members added to the stored mapping.</summary>
public sealed class LookupStorageTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private async Task<(QuorumApp App, Guid TenantId)> StartAsync()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        (await app.SetUpAsync()).Dispose();
        return (app, await TenantScope.TenantIdAsync(app));
    }

    private static KnowledgeBase NewKnowledgeBase(Guid tenantId, string name = "policies", KnowledgeBaseMapping? mapping = null)
    {
        var registration = KnowledgeBaseFixtures.Create(tenantId, name, "http://qdrant.test:6334", "docs", KnowledgeBaseScope.Tenant, []);
        return mapping is null
            ? registration
            : KnowledgeBase.Create(
                tenantId,
                name,
                registration.Description,
                registration.Kind,
                registration.Connection,
                registration.EmbeddingModel,
                null,
                mapping,
                KnowledgeBaseScope.Tenant,
                [],
                [],
                Now);
    }

    private static async Task<(Lookup Lookup, LookupSlice Slice)> AddLookupAsync(KnowledgeBasesDbContext db, Guid tenantId, KnowledgeBase knowledgeBase, int values = 3)
    {
        var lookup = Lookup.Computed(tenantId, knowledgeBase.Id, Guid.NewGuid(), LookupUnit.Documents, $"list-{Guid.NewGuid():N}", "A list of values.", Now);
        var slice = LookupSlice.Create(tenantId, lookup.Id, LookupSliceKey.WholeKnowledgeBase.Hash, LookupSliceKey.WholeKnowledgeBase.Filters, Now);
        db.Lookups.Add(lookup);
        db.LookupSlices.Add(slice);
        db.LookupValues.AddRange(Enumerable.Range(0, values).Select(i => LookupValue.Create(tenantId, slice.Id, generation: 1, position: i, value: $"supplier-{i}", documentCount: 100 - i)));
        await db.SaveChangesAsync(Ct);
        return (lookup, slice);
    }

    [Fact]
    public async Task LookupsSlicesAndValuesAreStoredAndReadBackWithEveryMember()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var knowledgeBase = NewKnowledgeBase(tenantId);

        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            db.KnowledgeBases.Add(knowledgeBase);
            var manual = Lookup.Manual(tenantId, "nationality", "Where people come from.", LookupScope.Tenant, [Guid.NewGuid()], Now);
            var manualSlice = LookupSlice.Create(tenantId, manual.Id, LookupSliceKey.WholeKnowledgeBase.Hash, LookupSliceKey.WholeKnowledgeBase.Filters, Now);
            db.Lookups.Add(manual);
            db.LookupSlices.Add(manualSlice);
            db.LookupValues.Add(LookupValue.Create(tenantId, manualSlice.Id, 1, 0, "IT", label: "Italy", synonyms: ["Italia", "italiana"], attributes: """{"continent":"Europe"}"""));
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var lookup = await db.Lookups.AsNoTracking().SingleAsync(Ct);
            var slice = await db.LookupSlices.AsNoTracking().SingleAsync(Ct);
            var value = await db.LookupValues.AsNoTracking().SingleAsync(Ct);

            Assert.Equal(LookupKind.Manual, lookup.Kind);
            Assert.Equal("nationality", lookup.Name);
            Assert.Equal(LookupScope.Tenant, lookup.Scope);
            Assert.Null(lookup.KnowledgeBaseId);
            Assert.Null(lookup.Unit);
            Assert.Single(lookup.ScopeTenantIds);
            Assert.Equal((LookupStatus.Pending, 0, false), (slice.Status, slice.CurrentGeneration, slice.Stale));
            Assert.Equal("[]", slice.Filters);
            Assert.Equal(("IT", "Italy"), (value.Value, value.Label));
            Assert.Equal(["Italia", "italiana"], value.Synonyms);
            Assert.Null(value.DocumentCount);
            Assert.Contains("Europe", value.Attributes, StringComparison.Ordinal);
        }
    }

    [Fact] // AC-41 (the tenant filter on the three tables)
    public async Task AnotherTenantSeesNoLookupSliceOrValue()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var knowledgeBase = NewKnowledgeBase(tenantId);
            db.KnowledgeBases.Add(knowledgeBase);
            await db.SaveChangesAsync(Ct);
            await AddLookupAsync(db, tenantId, knowledgeBase);
        }

        await using var other = TenantScope.Open(app, Guid.NewGuid());
        var otherDb = other.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();

        Assert.Empty(await otherDb.Lookups.ToListAsync(Ct));
        Assert.Empty(await otherDb.LookupSlices.ToListAsync(Ct));
        Assert.Empty(await otherDb.LookupValues.ToListAsync(Ct));
        Assert.Equal(1, await otherDb.Lookups.IgnoreQueryFilters().CountAsync(Ct));
        Assert.Equal(3, await otherDb.LookupValues.IgnoreQueryFilters().CountAsync(Ct));
    }

    [Fact] // AC-20 (storage side)
    public async Task DeletingAKnowledgeBaseDeletesItsLookupsSlicesAndValues()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        await using var scope = TenantScope.Open(app, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var kept = NewKnowledgeBase(tenantId, "kept");
        var removed = NewKnowledgeBase(tenantId, "removed");
        db.KnowledgeBases.AddRange(kept, removed);
        await db.SaveChangesAsync(Ct);
        await AddLookupAsync(db, tenantId, kept);
        await AddLookupAsync(db, tenantId, removed);

        db.KnowledgeBases.Remove(removed);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(1, await db.Lookups.CountAsync(Ct));
        Assert.Equal(1, await db.LookupSlices.CountAsync(Ct));
        Assert.Equal(3, await db.LookupValues.CountAsync(Ct));
    }

    [Fact]
    public async Task ALookupHasOneSliceForEachSetOfFilters()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        await using var scope = TenantScope.Open(app, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var knowledgeBase = NewKnowledgeBase(tenantId);
        db.KnowledgeBases.Add(knowledgeBase);
        await db.SaveChangesAsync(Ct);
        var (lookup, _) = await AddLookupAsync(db, tenantId, knowledgeBase, values: 0);

        db.LookupSlices.Add(LookupSlice.Create(tenantId, lookup.Id, LookupSliceKey.WholeKnowledgeBase.Hash, "[]", Now));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        Assert.True(exception.IsUniqueViolation());
    }

    [Fact]
    public async Task AFieldOfAKnowledgeBaseHasOneComputedLookup()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        await using var scope = TenantScope.Open(app, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var knowledgeBase = NewKnowledgeBase(tenantId);
        db.KnowledgeBases.Add(knowledgeBase);
        var fieldId = Guid.NewGuid();
        db.Lookups.Add(Lookup.Computed(tenantId, knowledgeBase.Id, fieldId, LookupUnit.Documents, $"list-{Guid.NewGuid():N}", "A list of values.", Now));
        await db.SaveChangesAsync(Ct);

        db.Lookups.Add(Lookup.Computed(tenantId, knowledgeBase.Id, fieldId, LookupUnit.Chunks, $"list-{Guid.NewGuid():N}", "A list of values.", Now));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        Assert.True(exception.IsUniqueViolation());
    }

    [Fact]
    public async Task TwoManualLookupsOfATenantCannotShareAName()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        await using var scope = TenantScope.Open(app, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        db.Lookups.Add(Lookup.Manual(tenantId, "nationality", "A list.", LookupScope.Global, [], Now));
        await db.SaveChangesAsync(Ct);

        db.Lookups.Add(Lookup.Manual(tenantId, "nationality", "A list.", LookupScope.Tenant, [], Now));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        Assert.True(exception.IsUniqueViolation());
    }

    [Fact]
    public async Task TheMembersOfTheMappingAreStoredAndAMappingSavedBeforeReadsAsNotSet()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        var mapping = new KnowledgeBaseMapping(
            new MappingRoles("text", null, null, null, null),
            [new MappedField("department", Guid.NewGuid(), Listable: true), new MappedField("year", Guid.NewGuid())],
            new CountedPoint("chunk_index", "0"),
            "name");
        var knowledgeBase = NewKnowledgeBase(tenantId, mapping: mapping);
        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            db.KnowledgeBases.Add(knowledgeBase);
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var stored = (await scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>().KnowledgeBases.AsNoTracking().SingleAsync(Ct)).Mapping;
            Assert.Equal(new CountedPoint("chunk_index", "0"), stored.CountedPoint);
            Assert.Equal("name", stored.EntityName);
            Assert.Equal([true, false], stored.Fields.Select(f => f.Listable));
        }

        // What a mapping looked like before spec 007: none of the new members in the JSON.
        var json = JsonNode.Parse(await ToolFixtures.ScalarAsync<string>(app, "SELECT \"Mapping\"::text FROM knowledge_base.knowledge_bases"))!.AsObject();
        json.Remove("CountedPoint");
        json.Remove("EntityName");
        foreach (var field in json["Fields"]!.AsArray())
        {
            field!.AsObject().Remove("Listable");
        }

        await ToolFixtures.ExecuteAsync(app, $"UPDATE knowledge_base.knowledge_bases SET \"Mapping\" = '{json.ToJsonString().Replace("'", "''", StringComparison.Ordinal)}'::jsonb");

        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var legacy = (await scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>().KnowledgeBases.AsNoTracking().SingleAsync(Ct)).Mapping;
            Assert.Null(legacy.CountedPoint);
            Assert.Null(legacy.EntityName);
            Assert.All(legacy.Fields, field => Assert.False(field.Listable));
        }
    }

    [Fact]
    public async Task AKnowledgeBaseSavedBeforeLookupsRefreshesEveryDay()
    {
        var (app, tenantId) = await StartAsync();
        await using var _ = app;
        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            db.KnowledgeBases.Add(NewKnowledgeBase(tenantId));
            await db.SaveChangesAsync(Ct);
        }

        await using var read = TenantScope.Open(app, tenantId);

        Assert.Equal(KnowledgeBase.DefaultLookupRefreshHours, (await read.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>().KnowledgeBases.SingleAsync(Ct)).LookupRefreshHours);
        Assert.Equal(24, KnowledgeBase.DefaultLookupRefreshHours);
    }

    [Fact]
    public async Task TheLookupsMigrationCanBeRevertedAndAppliedAgain()
    {
        var (app, _) = await StartAsync();
        await using var _ = app;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261022173048_ConnectionSettingsAsJson", Ct);
        Assert.Equal(0, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'knowledge_base' AND table_name LIKE 'lookup%'"));
        Assert.Equal(0, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'knowledge_base' AND column_name = 'LookupRefreshHours'"));

        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.Equal(3, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'knowledge_base' AND table_name LIKE 'lookup%'"));
        Assert.Equal(1, await ToolFixtures.ScalarAsync<long>(app, "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'knowledge_base' AND column_name = 'LookupRefreshHours'"));
    }
}
