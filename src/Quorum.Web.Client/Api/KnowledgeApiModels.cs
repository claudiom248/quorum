using System.Text.Json;

namespace Quorum.Web.Client.Api;

// Client-side copies of the knowledge base contract (spec 003): dictionary, knowledge bases and rag tools.

public static class DictionaryFieldTypes
{
    public const string Text = "Text";
    public const string Number = "Number";
    public const string Boolean = "Boolean";
    public const string Date = "Date";
    public const string TextList = "TextList";

    public static IReadOnlyList<string> All { get; } = [Text, Number, Boolean, Date, TextList];
}

public static class KnowledgeBaseStatuses
{
    public const string Available = "Available";
    public const string Unavailable = "Unavailable";
}

public static class KnowledgeBaseKinds
{
    public const string Qdrant = "Qdrant";
}

/// <param name="Scope"><c>Global</c> or <c>Tenant</c>.</param>
/// <param name="UsedByCount">How many knowledge bases and rag tools use the field: a field in use cannot be deleted.</param>
/// <param name="Cluster">The label that groups the field, or <see langword="null"/>; with the name it identifies the field.</param>
public sealed record DictionaryField(Guid Id, string Name, string Type, string? Description, string Scope, int UsedByCount, string? Cluster = null);

public sealed record DictionaryFieldRequest(string Name, string Type, string? Description, string Scope, string? Cluster = null);

public sealed record CreatedId(Guid Id);

/// <summary>Where a knowledge base lives. On an update an omitted <c>ApiKey</c> keeps the stored one.</summary>
public sealed record ConnectionRequest(string Url, string? ApiKey, string Collection, string? VectorName);

/// <param name="HasApiKey">Whether a key is stored. The key itself is never returned.</param>
public sealed record ConnectionInfo(string Url, string Collection, string? VectorName, bool HasApiKey);

/// <summary>The payload paths that carry the meaning Quorum needs; <c>Text</c> is the only required one.</summary>
public sealed record MappingRoles(string? Text, string? DocumentId, string? Title, string? Position, string? SourceUrl);

/// <param name="Schema">A pasted JSON Schema of the payload, as text. Omitted, the server samples the collection.</param>
public sealed record InspectRequest(string Kind, ConnectionRequest Connection, string EmbeddingModel, string? QueryPrefix, string? Schema);

public sealed record VectorInfo(string? Name, int Size, string Distance);

/// <param name="DictionaryFieldId">The visible dictionary field of that name, when there is one.</param>
/// <param name="Issues">Codes that block saving until the admin resolves them (collision, invalid or reserved name, type conflict).</param>
/// <param name="Cluster">The cluster of the preselected field.</param>
/// <param name="Candidates">Several visible fields have this name in different clusters, so none is preselected: the admin picks one.</param>
public sealed record DraftField(string Path, string Name, string Type, Guid? DictionaryFieldId, IReadOnlyList<string> Issues, string? Cluster = null, IReadOnlyList<FieldCandidate>? Candidates = null);

public sealed record FieldCandidate(Guid Id, string? Cluster, string Type);

public sealed record DraftMapping(MappingRoles Roles, IReadOnlyList<DraftField> Fields);

/// <summary>A dictionary field the draft needs and that does not exist yet.</summary>
public sealed record ProposedField(string Name, string Type, string? Cluster = null);

public sealed record KnowledgeBaseWarning(string Code, string Message, string? Path);

public sealed record InspectResult(
    bool Reachable,
    bool CollectionExists,
    IReadOnlyList<VectorInfo> Vectors,
    int? EmbeddingSize,
    bool? DimensionMatch,
    DraftMapping DraftMapping,
    IReadOnlyList<ProposedField> ProposedFields,
    IReadOnlyList<string> UnindexedFields,
    IReadOnlyList<KnowledgeBaseWarning> Warnings);

/// <param name="DictionaryFieldId">The dictionary field, when it exists; otherwise <paramref name="Name"/> finds it or names the one to create.</param>
/// <param name="Cluster">With <paramref name="Name"/>: the cluster of the field; none means a field with no cluster.</param>
public sealed record MappedFieldRequest(string Path, Guid? DictionaryFieldId, string? Name, string? Cluster = null);

public sealed record MappingRequest(MappingRoles Roles, IReadOnlyList<MappedFieldRequest> Fields);

/// <summary>A dictionary field the mapping names that does not exist yet: it is created only when listed here.</summary>
public sealed record CreateFieldRequest(string Name, string Type, string? Description = null, string? Cluster = null);

public sealed record KnowledgeBaseRequest(
    string Name,
    string Description,
    string Kind,
    ConnectionRequest Connection,
    string EmbeddingModel,
    string? QueryPrefix,
    MappingRequest Mapping,
    ToolScope Scope,
    IReadOnlyList<CreateFieldRequest> CreateFields);

/// <param name="Shared"><see langword="true"/> for a knowledge base of another tenant: only name, description and status are known.</param>
/// <param name="Scope">The kind of the scope (<c>Global</c>, <c>Tenant</c> or <c>Assistant</c>).</param>
public sealed record KnowledgeBaseListItem(
    Guid Id,
    string Name,
    string Description,
    string Status,
    bool Shared,
    string? Kind,
    string? Scope,
    DateTimeOffset? LastVerifiedAt,
    string? Warning);

public sealed record MappedFieldInfo(string Path, Guid DictionaryFieldId, string? Name, string? Cluster = null);

public sealed record MappingInfo(MappingRoles Roles, IReadOnlyList<MappedFieldInfo> Fields);

/// <summary>A knowledge base of this tenant in full. Of another tenant's only the first five members are present.</summary>
public sealed record KnowledgeBaseDetail(
    Guid Id,
    string Name,
    string Description,
    string Status,
    bool Shared,
    string? Kind,
    ConnectionInfo? Connection,
    string? EmbeddingModel,
    string? QueryPrefix,
    MappingInfo? Mapping,
    ToolScope? Scope,
    string? Warning,
    DateTimeOffset? LastVerifiedAt);

/// <summary>Saved even when the verification fails: the status and the warnings say what is wrong.</summary>
public sealed record RegisteredKnowledgeBase(Guid Id, string Status, IReadOnlyList<KnowledgeBaseWarning> Warnings);

public sealed record KnowledgeBaseVerification(string Status, IReadOnlyList<KnowledgeBaseWarning> Warnings);

/// <param name="Filters">The parameters of the generated tool schema: <c>department</c>, <c>year_min</c>...</param>
public sealed record TestSearchRequest(string Query, IReadOnlyDictionary<string, object>? Filters, int? Limit);

public sealed record TestSearchSource(string KnowledgeBase, string? DocumentId, string? Title, string? Position, string? Url);

public sealed record TestSearchHit(double Score, string Text, TestSearchSource Source);

public sealed record TestSearchResult(IReadOnlyList<TestSearchHit> Results, IReadOnlyList<string> Notes);

public sealed record RagKnowledgeBase(Guid Id, string Name);

/// <param name="Shared"><see langword="true"/> for a tool of another tenant made visible by its scope: read-only, its binding is not shown.</param>
public sealed record RagToolListItem(
    Guid Id,
    string Name,
    IReadOnlyList<RagKnowledgeBase> KnowledgeBases,
    ToolScope Scope,
    string State,
    string Policy,
    bool Shared,
    IReadOnlyList<ToolWarning> Warnings);

/// <summary>The definition the model gets: <c>Name</c> is what it sees (<c>rag__name</c>), <c>EstimatedTokens</c> what it takes of the context.</summary>
public sealed record RagToolDefinition(string Name, string Description, JsonElement InputSchema, int EstimatedTokens);

/// <param name="Alias">The parameter name the model sees: the alias given to the field, or its own name; <see langword="null"/> when the field is no longer visible.</param>
public sealed record ExposedFieldInfo(Guid FieldId, string? Alias);

/// <param name="Alias">The parameter name the model sees for the field; the field's own name when left out.</param>
public sealed record ExposedFieldRequest(Guid FieldId, string? Alias = null);

public sealed record RagToolDetail(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<ExposedFieldInfo> ExposedFields,
    JsonElement FixedFilters,
    int TopK,
    double? ScoreThreshold,
    int MaxResultCharacters,
    ToolScope Scope,
    string State,
    string Policy,
    RagToolDefinition? Definition,
    IReadOnlyList<ToolWarning> Warnings);

/// <param name="FixedFilters">Filters the model never sees, in the shape of the tool's parameters: <c>{"department": ["hr"], "year_min": 2024}</c>.</param>
public sealed record RagToolRequest(
    string Name,
    string Description,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<ExposedFieldRequest> ExposedFields,
    IReadOnlyDictionary<string, object>? FixedFilters,
    int? TopK,
    double? ScoreThreshold,
    int? MaxResultCharacters,
    ToolScope Scope,
    string? Policy);

public sealed record CreatedRagTool(Guid Id, RagToolDefinition? Definition);

/// <summary>Limits of a rag tool, as the server enforces them.</summary>
public static class RagToolLimits
{
    public const int MaxKnowledgeBases = 10;
    public const int MaxExposedFields = 10;
    public const int MaxTopK = 20;
    public const int DefaultTopK = 5;
    public const int DefaultMaxResultCharacters = 6_000;
    public const int MinMaxResultCharacters = 200;
    public const int MaxMaxResultCharacters = 50_000;
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 1_000;
}
