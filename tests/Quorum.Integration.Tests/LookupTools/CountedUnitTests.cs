using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Mapping;

namespace Quorum.Integration.Tests.LookupTools;

/// <summary>What a count says it counts, by the mapping of the knowledge base (spec 007 D-2, D-4).</summary>
public sealed class CountedUnitTests
{
    private static Target TargetOf(KnowledgeBaseMapping mapping) => new(
        KnowledgeBaseFixtures.Create(Guid.NewGuid(), "people", "http://localhost:6333", "people", KnowledgeBaseScope.Tenant, [], mapping: mapping),
        Adapter: null,
        Capability: null,
        Connection: null,
        Conditions: [],
        Problem: null);

    private static readonly MappingRoles Roles = new("text", null, null, null, null);

    [Fact]
    public void ACollectionOfEntitiesCountsDocumentsBecauseEachRecordIsOne()
    {
        var target = TargetOf(new KnowledgeBaseMapping(Roles, [], countedPoint: null, entityName: "name"));

        Assert.Equal("documents", target.Counted().Unit);
        Assert.Empty(target.Counted().Conditions);
    }

    [Fact]
    public void WithNeitherACountedPointNorAnEntityNameTheCountIsOfChunks()
    {
        var target = TargetOf(new KnowledgeBaseMapping(Roles, []));

        Assert.Equal("chunks", target.Counted().Unit);
    }

    [Fact]
    public void ACountedPointCountsDocumentsAndAddsItsCondition()
    {
        var target = TargetOf(new KnowledgeBaseMapping(Roles, [], new CountedPoint("is_first_chunk", "true")));

        var counted = target.Counted();

        Assert.Equal("documents", counted.Unit);
        Assert.Single(counted.Conditions);
    }

    [Fact]
    public void AComputedLookupOfACollectionOfEntitiesCountsDocuments()
    {
        var mapping = new KnowledgeBaseMapping(Roles, [], countedPoint: null, entityName: "name");

        Assert.Equal(LookupUnit.Documents, LookupComputer.UnitOf(mapping));
    }

    [Fact]
    public void AComputedLookupWithNeitherACountedPointNorAnEntityNameCountsChunks()
    {
        Assert.Equal(LookupUnit.Chunks, LookupComputer.UnitOf(new KnowledgeBaseMapping(Roles, [])));
    }
}
