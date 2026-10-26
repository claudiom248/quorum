using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Mapping;

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

/// <summary>
/// The types a store declares for some properties (the columns of a relation), which are better evidence than the values of a sample:
/// a column of dates that holds text in the first rows is still a date column, and a column that is empty in the sample still has a type.
/// </summary>
/// <param name="Properties">Declared properties, in the order the store lists them.</param>
/// <param name="Warnings">What the store could not map, with the cause.</param>
internal sealed record DeclaredProperties(IReadOnlyList<DiscoveredProperty> Properties, IReadOnlyList<InspectWarning> Warnings)
{
    /// <summary>The sampled discovery with the declared types in place of what the sample inferred for the same paths.</summary>
    public Discovery Apply(Discovery sampled)
    {
        ArgumentNullException.ThrowIfNull(sampled);

        var declared = Properties.Select(property => property.Path).ToHashSet(StringComparer.Ordinal);
        var properties = Properties.Concat(sampled.Properties.Where(property => !declared.Contains(property.Path))).ToList();
        var warnings = sampled.Warnings.Where(warning => warning.Path is null || !declared.Contains(warning.Path)).Concat(Warnings).ToList();
        return new Discovery(properties, warnings);
    }
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
    public const string QdrantLookupsVersion = "knowledge_base.qdrant_lookups_version";
    public const string LookupFieldUnindexed = "knowledge_base.lookup_field_unindexed";
    public const string EmbeddingFailed = "knowledge_base.embedding_failed";
    public const string DimensionMismatch = "knowledge_base.dimension_mismatch";
    public const string VectorNameRequired = "knowledge_base.vector_name_required";
    public const string VectorNotFound = "knowledge_base.vector_not_found";
    public const string MappedPathMissing = "knowledge_base.mapped_path_missing";
    public const string PgVectorUnreachable = "knowledge_base.pgvector_unreachable";
    public const string PgVectorUnauthorized = "knowledge_base.pgvector_unauthorized";
    public const string PgVectorUnexpected = "knowledge_base.pgvector_unexpected_response";
    public const string PgVectorExtensionMissing = "knowledge_base.pgvector_extension_missing";
    public const string PgVectorRelationNotFound = "knowledge_base.pgvector_relation_not_found";
    public const string PgVectorEmptyRelation = "knowledge_base.pgvector_empty_relation";
    public const string PgVectorDimensionMismatch = "knowledge_base.pgvector_dimension_mismatch";
    public const string PgVectorDimensionsUnknown = "knowledge_base.pgvector_dimensions_unknown";
    public const string PgVectorColumnNotMapped = "knowledge_base.pgvector_column_not_mapped";
    public const string PgVectorColumnNameUnsupported = "knowledge_base.pgvector_column_name_unsupported";
    public const string PgVectorDistanceDefaulted = "knowledge_base.pgvector_distance_defaulted";
    public const string PgVectorFieldUnindexed = "knowledge_base.pgvector_field_unindexed";
    public const string VectorColumnRequired = "knowledge_base.vector_column_required";
    public const string VectorColumnNotFound = "knowledge_base.vector_column_not_found";
}
