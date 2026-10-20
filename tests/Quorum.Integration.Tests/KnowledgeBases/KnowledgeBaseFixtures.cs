using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;

namespace Quorum.Integration.Tests.KnowledgeBases;

internal static class KnowledgeBaseFixtures
{
    /// <summary>An available registration written straight to the database, for tests about who can see what.</summary>
    public static KnowledgeBase Create(
        Guid tenantId,
        string name,
        string url,
        string collection,
        KnowledgeBaseScope scope,
        IEnumerable<Guid> scopeTenantIds,
        IEnumerable<Guid>? mappedFieldIds = null)
    {
        var now = DateTimeOffset.UtcNow;
        var registration = KnowledgeBase.Create(
            tenantId,
            name,
            $"Another tenant's {name}.",
            new QdrantConnectionSettings(url, collection, VectorName: null, ProtectedApiKey: "protected-s3cret"),
            "nomic-embed-text",
            queryPrefix: null,
            new KnowledgeBaseMapping(new MappingRoles("text", null, null, null, null), [.. (mappedFieldIds ?? []).Select((id, index) => new MappedField($"field{index}", id))]),
            scope,
            scopeTenantIds,
            scopeAssistantIds: [],
            now);
        registration.MarkAvailable(now);
        return registration;
    }
}
