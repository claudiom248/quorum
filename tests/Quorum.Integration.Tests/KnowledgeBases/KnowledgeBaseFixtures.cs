using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;

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
        IEnumerable<Guid>? mappedFieldIds = null,
        int lookupRefreshHours = KnowledgeBase.DefaultLookupRefreshHours,
        IEnumerable<Guid>? scopeAssistantIds = null)
    {
        var now = DateTimeOffset.UtcNow;
        var registration = KnowledgeBase.Create(
            tenantId,
            name,
            $"Another tenant's {name}.",
            KnowledgeBaseKind.Qdrant,
            ConnectionSettings.From(new QdrantConnectionSettings(url, collection, VectorName: null, ProtectedApiKey: "protected-s3cret")),
            "nomic-embed-text",
            queryPrefix: null,
            new KnowledgeBaseMapping(new MappingRoles("text", null, null, null, null), [.. (mappedFieldIds ?? []).Select((id, index) => new MappedField($"field{index}", id))]),
            scope,
            scopeTenantIds,
            scopeAssistantIds: scopeAssistantIds ?? [],
            now,
            lookupRefreshHours);
        registration.MarkAvailable(now);
        return registration;
    }
}
