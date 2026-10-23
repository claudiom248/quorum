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

    /// <summary>The documents of the knowledge bases that match the filters, a page at a time, computed now against the store (spec 007 D-7).</summary>
    Task<LiveDocuments> DocumentsAsync(LiveDocumentsRequest request, CancellationToken cancellationToken);

    /// <summary>How many documents (or chunks, when the knowledge base has no way to tell documents apart) match the filters, exactly, computed now.</summary>
    Task<LiveCount> CountAsync(LiveCountRequest request, CancellationToken cancellationToken);

    /// <summary>The values of a computed lookup's field among the documents that match the filters, computed now instead of read from the stored list.</summary>
    Task<LiveValues> LiveValuesAsync(LiveValuesRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Which of the fields a tool filters on have no index in the stores of its knowledge bases, so that a live operation filtering on
    /// them may be slow (spec 007 D-8). A store that cannot be reached is not reported: there is nothing to say about it yet.
    /// </summary>
    Task<IReadOnlyList<UnindexedFilter>> UnindexedFiltersAsync(UnindexedFiltersQuery query, CancellationToken cancellationToken);
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
/// <param name="FieldId">The dictionary field a computed lookup lists; <see langword="null"/> for the others.</param>
/// <param name="SortableAttributes">The attributes an entity lookup can be ordered by (numbers and dates), under the names it stores them with.</param>
public sealed record LookupInfo(
    Guid Id,
    string Name,
    LookupKindInfo Kind,
    Guid? KnowledgeBaseId,
    string? KnowledgeBaseName,
    string? Description,
    string? Unit,
    Guid? FieldId = null,
    IReadOnlyList<string>? SortableAttributes = null);

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

    /// <summary>By an attribute of an entity lookup, named in <see cref="LookupReadRequest.Attribute"/>.</summary>
    Attribute,
}

/// <param name="Name">One of <see cref="LookupInfo.SortableAttributes"/>.</param>
/// <param name="Descending">The largest or the latest first.</param>
public sealed record LookupAttributeOrder(string Name, bool Descending);

/// <param name="Search">A case-insensitive substring of a value, its label or one of its synonyms.</param>
/// <param name="Limit">Values in the page; the reader keeps it between 1 and 200.</param>
/// <param name="Cursor">The opaque cursor the previous page returned.</param>
/// <param name="Attribute">With <see cref="LookupSortOrder.Attribute"/>: the attribute to order by.</param>
public sealed record LookupReadRequest(
    Guid TenantId,
    Guid? AssistantId,
    Guid LookupId,
    LookupSliceFilters Filters,
    string? Search,
    LookupSortOrder Sort,
    int Limit,
    string? Cursor,
    LookupAttributeOrder? Attribute = null);

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

    /// <summary>The lookup cannot be ordered by the attribute asked for.</summary>
    InvalidSort,
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
/// <param name="Live">The list was worked out now among the documents that match filters, not read from the stored one.</param>
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
    bool CursorRestarted = false,
    bool Live = false)
{
    public static LookupRead Of(LookupReadOutcome outcome, string? name = null, string? knowledgeBase = null) => new(outcome, name, knowledgeBase);
}

/// <summary>
/// What the live operations share: who asks and the filters, in the shape of <see cref="KnowledgeBaseSearchRequest"/>. The model's filters
/// and the tool's fixed ones always apply together, and a model's value for a field the tool fixed can only narrow it.
/// </summary>
/// <param name="Filters">The model's filters by parameter name: <c>department</c>, <c>year_min</c>... A <see langword="null"/> or empty value is no filter.</param>
/// <param name="FixedFilters">The tool's own, never visible to the model.</param>
/// <param name="FilterFields">The parameters the model was offered (the aliases of the exposed fields) with the field each stands for.</param>
/// <param name="FixedFilterFieldIds">The fields the fixed filters name.</param>
public sealed record LiveScope(
    Guid TenantId,
    Guid AssistantId,
    IReadOnlyDictionary<string, JsonElement> Filters,
    IReadOnlyDictionary<string, JsonElement> FixedFilters,
    IReadOnlyDictionary<string, Guid> FilterFields,
    IReadOnlyList<Guid> FixedFilterFieldIds);

public enum LiveOutcome
{
    Ok,

    /// <summary>None of the knowledge bases can be used: they do not exist, are not visible or their kind has no lookups.</summary>
    NotAvailable,

    /// <summary>A filter of the model is not valid; the message says why.</summary>
    InvalidFilter,

    /// <summary>A filter that the tool always applies is not valid any more.</summary>
    InvalidFixedFilter,

    /// <summary>No knowledge base maps the fields the filters use, so none could apply them.</summary>
    FilterUnsupported,

    /// <summary>The cursor is not one these knowledge bases issued.</summary>
    InvalidCursor,

    /// <summary>It did not finish within the limit (a filter on a field without an index is the usual cause).</summary>
    TimedOut,

    /// <summary>Every knowledge base failed; the message has the reasons.</summary>
    Failed,

    /// <summary>With the live values: the lookup is not a computed one, so it has no field to list among documents.</summary>
    NotComputed,
}

/// <param name="KnowledgeBaseIds">Only these are queried, among the ones the assistant can see.</param>
/// <param name="ReturnFieldIds">The exposed fields whose values come back with each document.</param>
/// <param name="Limit">Documents in the page; 1 to 200.</param>
/// <param name="Cursor">The opaque cursor the previous page returned.</param>
public sealed record LiveDocumentsRequest(
    LiveScope Scope,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    IReadOnlyList<Guid> ReturnFieldIds,
    int Limit,
    string? Cursor);

/// <param name="Fields">The values of the fields asked for that the document has, by field id; lists are joined by commas.</param>
public sealed record LiveDocument(string KnowledgeBase, string? Id, string? Title, string? Url, IReadOnlyDictionary<Guid, string> Fields);

/// <param name="Message">With a failure: written for the model, with no address, key or internal detail.</param>
/// <param name="Cursor"><see langword="null"/> on the last page. A page may hold fewer documents than the limit and still have a cursor.</param>
/// <param name="Skipped">Knowledge bases left out because they do not map a field of the filters, or have no lookups.</param>
/// <param name="Failed">Knowledge bases that could not be queried, with why.</param>
public sealed record LiveDocuments(
    LiveOutcome Outcome,
    string? Message = null,
    IReadOnlyList<LiveDocument>? Documents = null,
    string? Cursor = null,
    IReadOnlyList<string>? Skipped = null,
    IReadOnlyList<FailedKnowledgeBase>? Failed = null);

public sealed record LiveCountRequest(LiveScope Scope, IReadOnlyList<Guid> KnowledgeBaseIds);

/// <param name="Unit"><c>documents</c> or <c>chunks</c>; counts of different units are never added.</param>
public sealed record LiveKnowledgeBaseCount(string KnowledgeBase, long Count, string Unit);

public sealed record LiveCount(
    LiveOutcome Outcome,
    string? Message = null,
    IReadOnlyList<LiveKnowledgeBaseCount>? Counts = null,
    IReadOnlyList<string>? Skipped = null,
    IReadOnlyList<FailedKnowledgeBase>? Failed = null);

/// <param name="Search">A case-insensitive substring of a value.</param>
/// <param name="Limit">Values in the answer; 1 to 200.</param>
public sealed record LiveValuesRequest(LiveScope Scope, Guid LookupId, string? Search, LookupSortOrder Sort, int Limit);

/// <param name="Truncated">There are more values than the answer holds, or the scan stopped at its cap.</param>
/// <param name="ValueCount">Values among the matching documents, before the limit.</param>
public sealed record LiveValues(
    LiveOutcome Outcome,
    string? Message = null,
    string? Name = null,
    string? KnowledgeBase = null,
    string? Unit = null,
    int ValueCount = 0,
    bool Truncated = false,
    IReadOnlyList<LookupReadValue>? Values = null);

/// <param name="IncludeDocumentId">Also report the knowledge bases whose <c>documentId</c> path has no index, for a tool that restricts searches to documents (spec 007 D-14).</param>
public sealed record UnindexedFiltersQuery(Guid TenantId, IReadOnlyList<Guid> KnowledgeBaseIds, IReadOnlyList<Guid> FieldIds, bool IncludeDocumentId = false);

/// <param name="FieldId"><see cref="Guid.Empty"/> for the document id, which is a role of the knowledge base and not a dictionary field.</param>
/// <param name="Path">The payload path the field is mapped to in that knowledge base.</param>
public sealed record UnindexedFilter(Guid FieldId, string FieldName, Guid KnowledgeBaseId, string KnowledgeBaseName, string Path);
