using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Persistence.Migrations;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>The statements of the consolidation migration, run against lookups as they were before it: their names and descriptions come from the knowledge base and the field (spec 012 D-2, D-10).</summary>
public sealed class LookupBackfillTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-25
    public async Task ExistingComputedListsAreNamedAfterTheirKnowledgeBaseAndField()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        await using var _ = app;
        (await app.SetUpAsync()).Dispose();
        var tenantId = await TenantScope.TenantIdAsync(app);
        await using var scope = TenantScope.Open(app, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var now = DateTimeOffset.UtcNow;
        var knowledgeBase = KnowledgeBaseFixtures.Create(tenantId, "policies", "http://qdrant.test:6334", "docs", KnowledgeBaseScope.Tenant, []);
        var field = DictionaryField.Create(tenantId, "country", DictionaryFieldType.Text, null, DictionaryFieldScope.Tenant, now);
        db.DictionaryFields.Add(field);
        db.KnowledgeBases.Add(knowledgeBase);
        db.Lookups.Add(Lookup.Computed(tenantId, knowledgeBase.Id, field.Id, LookupUnit.Documents, "placeholder-" + Guid.NewGuid().ToString("N"), "placeholder", now));
        await db.SaveChangesAsync(Ct);

        // What the rows looked like before spec 012: a name of their own, no proper description.
        await db.Database.ExecuteSqlRawAsync("""UPDATE knowledge_base.lookups SET "Name" = 'placeholder', "Description" = '' """, Ct);
        await db.Database.ExecuteSqlRawAsync(ConsolidateLookupsBackfill.ComputedNames, Ct);
        await db.Database.ExecuteSqlRawAsync(ConsolidateLookupsBackfill.Descriptions, Ct);

        var lookup = await db.Lookups.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.KnowledgeBaseId == knowledgeBase.Id, Ct);
        Assert.Equal("policies.country", lookup.Name);
        Assert.Equal("Distinct values of country in policies", lookup.Description);
    }

    [Fact] // AC-25
    public async Task AnEntityListGetsTheAttributesItsKnowledgeBaseMapsAsAttributes()
    {
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        await using var _ = app;
        (await app.SetUpAsync()).Dispose();
        var tenantId = await TenantScope.TenantIdAsync(app);
        await using var scope = TenantScope.Open(app, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var now = DateTimeOffset.UtcNow;
        var country = DictionaryField.Create(tenantId, "country", DictionaryFieldType.Text, null, DictionaryFieldScope.Tenant, now);
        var contracts = DictionaryField.Create(tenantId, "contract_count", DictionaryFieldType.Number, null, DictionaryFieldScope.Tenant, now);
        var mapping = new KnowledgeBaseMapping(
            new MappingRoles("text", null, null, null, null),
            [new MappedField("country", country.Id, false, true), new MappedField("contract_count", contracts.Id, false, true)],
            null,
            "name");
        var knowledgeBase = KnowledgeBaseFixtures.Create(tenantId, "suppliers", "http://qdrant.test:6334", "docs", KnowledgeBaseScope.Tenant, [], mapping: mapping);
        db.DictionaryFields.AddRange(country, contracts);
        db.KnowledgeBases.Add(knowledgeBase);
        db.Lookups.Add(Lookup.Entity(tenantId, knowledgeBase.Id, "placeholder", "placeholder", now));
        await db.SaveChangesAsync(Ct);

        await db.Database.ExecuteSqlRawAsync("""UPDATE knowledge_base.lookups SET "Attributes" = NULL""", Ct);
        await db.Database.ExecuteSqlRawAsync(ConsolidateLookupsBackfill.EntityAttributes, Ct);
        await db.Database.ExecuteSqlRawAsync(ConsolidateLookupsBackfill.EmptyAttributes, Ct);

        var lookup = await db.Lookups.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.KnowledgeBaseId == knowledgeBase.Id, Ct);
        Assert.Equal([("contract_count", LookupAttributeType.Number), ("country", LookupAttributeType.Text)], lookup.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => (a.Key, a.Type)));
    }
}
