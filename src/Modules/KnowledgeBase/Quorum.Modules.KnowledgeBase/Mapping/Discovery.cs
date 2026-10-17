using Quorum.Modules.KnowledgeBase.Domain;

namespace Quorum.Modules.KnowledgeBase.Mapping;

/// <summary>A scalar or list-of-scalars property of a payload, found by sampling points or by reading a schema.</summary>
/// <param name="Path">Payload path in dot notation (<c>meta.year</c>).</param>
internal sealed record DiscoveredProperty(string Path, DictionaryFieldType Type);

/// <summary>Something the admin should know about before saving; <see cref="Code"/> is stable, the message says the cause and what follows from it.</summary>
internal sealed record InspectWarning(string Code, string Message, string? Path = null);

/// <summary>What a payload source (sampled points, a pasted schema) says about the payload, before the naming conventions are applied.</summary>
internal sealed record Discovery(IReadOnlyList<DiscoveredProperty> Properties, IReadOnlyList<InspectWarning> Warnings)
{
    public static Discovery Empty { get; } = new([], []);
}

/// <summary>The warning codes of inspection, shared by the sources so that the review screen treats them alike.</summary>
internal static class WarningCodes
{
    public const string MixedTypes = "knowledge_base.mixed_types";
    public const string NoValues = "knowledge_base.no_values";
    public const string PropertyNotMapped = "knowledge_base.property_not_mapped";
    public const string UnsupportedPropertyName = "knowledge_base.unsupported_property_name";
    public const string UnsupportedSchemaKeyword = "knowledge_base.unsupported_schema_keyword";
    public const string TooManyProperties = "knowledge_base.too_many_properties";
    public const string FieldUnindexed = "knowledge_base.field_unindexed";
    public const string EmptyCollection = "knowledge_base.empty_collection";
    public const string CollectionNotFound = "knowledge_base.collection_not_found";
    public const string QdrantUnreachable = "knowledge_base.qdrant_unreachable";
    public const string QdrantUnauthorized = "knowledge_base.qdrant_unauthorized";
    public const string QdrantUnexpected = "knowledge_base.qdrant_unexpected_response";
    public const string QdrantVersion = "knowledge_base.qdrant_version_unsupported";
    public const string EmbeddingFailed = "knowledge_base.embedding_failed";
    public const string DimensionMismatch = "knowledge_base.dimension_mismatch";
    public const string VectorNameRequired = "knowledge_base.vector_name_required";
    public const string VectorNotFound = "knowledge_base.vector_not_found";
}
