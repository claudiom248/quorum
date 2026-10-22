using System.Text.Json;

namespace Quorum.Modules.KnowledgeBases.Contracts;

/// <summary>
/// What the lookup tool of the Agents module needs from the lookups of KnowledgeBases (spec 007): to list the lookups a tenant can
/// see, to say which slices a tool uses, and to read a page of a lookup. Like the catalog and the search, every call answers for the
/// tenant of the current request only.
/// </summary>
public interface IKnowledgeBaseLookups
{
    /// <summary>The lookups the tenant (and the assistant, when one is answering) can see now, or just those of <see cref="LookupListQuery.LookupIds"/>. What it cannot see is absent.</summary>
    Task<IReadOnlyList<LookupInfo>> ListAsync(LookupListQuery query, CancellationToken cancellationToken);

    /// <summary>Which lookups are not visible to everyone in the scope a tool will have (spec 003 "Rule between scopes"); a lookup that does not exist is hidden like any other.</summary>
    Task<IReadOnlyList<Guid>> HiddenAsync(LookupScopeQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Says that a tool uses exactly these lookups for the given fixed filters (spec 007 D-6): the slices it needs are created and
    /// computed in the background, the ones it no longer needs lose it as a user and are removed when nothing else uses them. Nothing
    /// is saved when the result is not <see cref="SliceDeclarationOutcome.Declared"/>. It joins the open platform transaction.
    /// </summary>
    Task<SliceDeclaration> DeclareSlicesAsync(SliceDeclarationRequest request, CancellationToken cancellationToken);

    /// <summary>A tool is deleted: its slices lose it as a user. It joins the open platform transaction.</summary>
    Task ReleaseSlicesAsync(Guid tenantId, Guid toolId, CancellationToken cancellationToken);

    /// <summary>Reads a page of one lookup from the cache or the tables, never from the store and never waiting for a computation.</summary>
    Task<LookupRead> ReadAsync(LookupReadRequest request, CancellationToken cancellationToken);
}

public enum LookupKindInfo
{
    Computed,
    Entity,
    Manual,
}

/// <param name="Name">What the model sees by default: the dictionary field's name for a computed lookup, the knowledge base's for an entity lookup, the typed name for a manual one.</param>
/// <param name="KnowledgeBaseId"><see langword="null"/> for a manual lookup.</param>
/// <param name="Unit"><c>documents</c> or <c>chunks</c> for a computed lookup; <see langword="null"/> for the others, which have no counts.</param>
public sealed record LookupInfo(
    Guid Id,
    string Name,
    LookupKindInfo Kind,
    Guid? KnowledgeBaseId,
    string? KnowledgeBaseName,
    string? Description,
    string? Unit);

/// <param name="TenantId">The tenant asking; it must be the tenant of the current request.</param>
/// <param name="AssistantId">The assistant answering, or <see langword="null"/> for an administrator looking at a configuration.</param>
/// <param name="LookupIds">Only these, or <see langword="null"/> for every visible lookup.</param>
public sealed record LookupListQuery(Guid TenantId, Guid? AssistantId, IReadOnlyList<Guid>? LookupIds = null);

/// <param name="TenantId">The owner of the binding; it must be the tenant of the current request.</param>
public sealed record LookupScopeQuery(Guid TenantId, BindingScope Scope, IReadOnlyList<Guid> LookupIds);

/// <summary>
/// The fixed filters of a tool in the shape of the fields of <see cref="FixedFilterQuery"/>: they are what makes a slice, and the same
/// ones are read again at every call, so a slice is found by its filters and not by an id the tool would have to keep.
/// </summary>
/// <param name="Filters">The tool's fixed filters by parameter name: <c>department</c>, <c>year_min</c>...</param>
/// <param name="Aliases">The parameter names the model sees for the exposed fields: alias to field id.</param>
/// <param name="FieldIds">The dictionary fields the fixed filters name.</param>
public sealed record LookupSliceFilters(
    IReadOnlyDictionary<string, JsonElement> Filters,
    IReadOnlyDictionary<string, Guid> Aliases,
    IReadOnlyList<Guid> FieldIds)
{
    /// <summary>A tool with no fixed filters.</summary>
    public static LookupSliceFilters None { get; } = new(new Dictionary<string, JsonElement>(), new Dictionary<string, Guid>(), []);
}

public sealed record SliceDeclarationRequest(Guid TenantId, Guid ToolId, IReadOnlyList<Guid> LookupIds, LookupSliceFilters Filters);

public enum SliceDeclarationOutcome
{
    Declared,

    /// <summary>A knowledge base would have more slices than the limit (spec 007 D-6).</summary>
    TooManySlices,

    /// <summary>The fixed filters do not fit the fields they name.</summary>
    InvalidFilters,
}

/// <param name="KnowledgeBaseName">With <see cref="SliceDeclarationOutcome.TooManySlices"/>: the knowledge base that is full.</param>
/// <param name="Problem">With <see cref="SliceDeclarationOutcome.InvalidFilters"/>: what is wrong, written for the administrator.</param>
public sealed record SliceDeclaration(SliceDeclarationOutcome Outcome, string? KnowledgeBaseName = null, string? Problem = null)
{
    public bool IsDeclared => Outcome == SliceDeclarationOutcome.Declared;
}

public enum LookupSortOrder
{
    /// <summary>By the number of documents, then by value.</summary>
    Count,

    Name,
}

/// <param name="Search">A case-insensitive substring of a value, its label or one of its synonyms.</param>
/// <param name="Limit">Values in the page; the reader keeps it between 1 and 200.</param>
/// <param name="Cursor">The opaque cursor the previous page returned.</param>
public sealed record LookupReadRequest(
    Guid TenantId,
    Guid? AssistantId,
    Guid LookupId,
    LookupSliceFilters Filters,
    string? Search,
    LookupSortOrder Sort,
    int Limit,
    string? Cursor);

public enum LookupReadOutcome
{
    Ok,

    /// <summary>The lookup has no list yet, or its slice is not computed yet.</summary>
    Preparing,

    /// <summary>The computation failed and there is no earlier list to serve.</summary>
    Failed,

    /// <summary>The lookup does not exist or the tenant cannot see it.</summary>
    NotAvailable,

    /// <summary>The knowledge base does not map a field of the tool's fixed filters, so it has no such slice and is skipped.</summary>
    KnowledgeBaseSkipped,

    /// <summary>The fixed filters of the tool do not fit the fields they name any more.</summary>
    InvalidFilters,

    /// <summary>The cursor is not one this lookup issued.</summary>
    InvalidCursor,
}

public sealed record LookupReadValue(string Value, string? Label, IReadOnlyList<string> Synonyms, int? Count, string? Attributes);

/// <param name="Name">The lookup's own name; <see langword="null"/> when it is not available.</param>
/// <param name="KnowledgeBase">The knowledge base of a computed or entity lookup.</param>
/// <param name="Unit"><c>documents</c> or <c>chunks</c>; <see langword="null"/> for a lookup with no counts.</param>
/// <param name="AsOf">When the list was computed.</param>
/// <param name="Stale">The last computation failed, so the list is older than it should be.</param>
/// <param name="Truncated">The list was cut at the installation's ceiling, or the scan stopped at its cap.</param>
/// <param name="ValueCount">Values in the whole list, not in the page.</param>
/// <param name="Cursor"><see langword="null"/> on the last page.</param>
/// <param name="CursorRestarted">The cursor belonged to an older list, so this page starts again from the top.</param>
public sealed record LookupRead(
    LookupReadOutcome Outcome,
    string? Name = null,
    string? KnowledgeBase = null,
    string? Unit = null,
    DateTimeOffset? AsOf = null,
    bool Stale = false,
    bool Truncated = false,
    int ValueCount = 0,
    IReadOnlyList<LookupReadValue>? Values = null,
    string? Cursor = null,
    bool CursorRestarted = false)
{
    public static LookupRead Of(LookupReadOutcome outcome, string? name = null, string? knowledgeBase = null) => new(outcome, name, knowledgeBase);
}
