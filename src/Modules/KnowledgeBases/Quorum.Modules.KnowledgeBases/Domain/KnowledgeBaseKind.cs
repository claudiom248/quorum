namespace Quorum.Modules.KnowledgeBases.Domain;

/// <summary>
/// What a knowledge base is stored in. The kind decides its connection settings, discovery and search (D-2); Qdrant and PgVector
/// are implemented, the others are recorded in <c>docs/deferred.md</c>.
/// </summary>
internal enum KnowledgeBaseKind
{
    Qdrant,
    PgVector,
}
