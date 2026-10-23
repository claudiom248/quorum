using System.Text.Json;

namespace Quorum.Web.Client.Api;

// Client-side copies of the lookup tool contract (spec 007 D-12): a tool that hands lists of values to a model.

public sealed record NamedId(Guid Id, string Name);

/// <param name="Shared"><see langword="true"/> for a tool of another tenant made visible by its scope: read-only, its binding is not shown.</param>
public sealed record LookupToolListItem(
    Guid Id,
    string Name,
    IReadOnlyList<NamedId> Lookups,
    IReadOnlyList<NamedId> KnowledgeBases,
    ToolScope Scope,
    string State,
    string Policy,
    bool Shared,
    IReadOnlyList<ToolWarning> Warnings);

/// <param name="Name">What the model sees: <c>lookup__name</c>. <paramref name="EstimatedTokens"/> is what it takes of the context.</param>
public sealed record LookupToolDefinitionInfo(string Name, string Description, JsonElement InputSchema, int EstimatedTokens);

/// <param name="Alias">The name the model sees for the lookup, when one was given.</param>
public sealed record BoundLookup(Guid LookupId, string? Alias);

public sealed record LookupToolDetail(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<BoundLookup> Lookups,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<ExposedFieldInfo> ExposedFields,
    JsonElement FixedFilters,
    ToolScope Scope,
    string State,
    string Policy,
    LookupToolDefinitionInfo? Definition,
    IReadOnlyList<ToolWarning> Warnings);

public sealed record LookupBindingRequest(Guid LookupId, string? Alias);

/// <param name="Lookups">The lists the tool reads, each with the alias the model sees for it.</param>
/// <param name="KnowledgeBaseIds">The knowledge bases the live operations (documents, count, values with filters) query.</param>
/// <param name="FixedFilters">Filters the model never sees, in the shape of the tool's parameters: <c>{"department": ["hr"]}</c>.</param>
public sealed record LookupToolRequest(
    string Name,
    string Description,
    IReadOnlyList<LookupBindingRequest> Lookups,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<ExposedFieldRequest> ExposedFields,
    IReadOnlyDictionary<string, object>? FixedFilters,
    ToolScope Scope,
    string? Policy);

public sealed record CreatedLookupTool(Guid Id, LookupToolDefinitionInfo? Definition, IReadOnlyList<ToolWarning> Warnings);

/// <summary>Limits of a lookup tool, as the server enforces them.</summary>
public static class LookupToolLimits
{
    public const int MaxLookups = 10;
    public const int MaxKnowledgeBases = 10;
    public const int MaxExposedFields = 10;
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 1_000;
}
