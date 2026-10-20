using System.Text.Json;

namespace Quorum.Modules.KnowledgeBases.Contracts;

/// <summary>What the Agents module needs to know about knowledge bases to describe a rag tool and to validate a binding.</summary>
public interface IKnowledgeBaseCatalog
{
    /// <summary>
    /// Describes the knowledge bases and dictionary fields of a binding as the tenant sees them now. What the tenant cannot see
    /// (deleted, scope narrowed, never existed) is simply absent, so a caller can tell nothing about it.
    /// </summary>
    Task<BindingDescription> DescribeAsync(BindingQuery query, CancellationToken cancellationToken);

    /// <summary>Which knowledge bases and fields of a binding are not visible to the whole scope it will have (spec 003 "Rule between scopes").</summary>
    Task<ScopeCheck> CheckScopeAsync(ScopeCheckQuery query, CancellationToken cancellationToken);

    /// <summary>Whether the fixed filters of a binding fit the types of the fields they name, with the same rules a model's filters follow, except that none of them may be empty.</summary>
    Task<FixedFilterCheck> CheckFixedFiltersAsync(FixedFilterQuery query, CancellationToken cancellationToken);
}

/// <param name="TenantId">The tenant describing; it must be the tenant of the current request.</param>
/// <param name="AssistantId">The assistant answering, or <see langword="null"/> for an administrator looking at a configuration, who sees the owner's assistant-scoped knowledge bases.</param>
/// <param name="FieldIds">Fields to describe besides the ones the knowledge bases map.</param>
public sealed record BindingQuery(Guid TenantId, Guid? AssistantId, IReadOnlyList<Guid> KnowledgeBaseIds, IReadOnlyList<Guid> FieldIds);

public sealed record BindingDescription(IReadOnlyList<KnowledgeBaseInfo> KnowledgeBases, IReadOnlyList<FilterFieldInfo> Fields);

/// <param name="Description">Written by the administrator for the model.</param>
/// <param name="FieldIds">The dictionary fields the knowledge base maps and the tenant can see.</param>
public sealed record KnowledgeBaseInfo(Guid Id, string Name, string Description, IReadOnlyList<Guid> FieldIds);

public enum FilterFieldType
{
    Text,
    Number,
    Boolean,
    Date,
    TextList,
}

public sealed record FilterFieldInfo(Guid Id, string Name, FilterFieldType Type, string? Description);

public enum BindingScopeKind
{
    Global,
    Tenant,
    Assistant,
}

/// <param name="TenantIds">With <see cref="BindingScopeKind.Tenant"/>: the tenants besides the owner.</param>
/// <param name="AssistantIds">With <see cref="BindingScopeKind.Assistant"/>: the owner's assistants.</param>
public sealed record BindingScope(BindingScopeKind Kind, IReadOnlyList<Guid> TenantIds, IReadOnlyList<Guid> AssistantIds);

/// <param name="TenantId">The owner of the binding; it must be the tenant of the current request.</param>
public sealed record ScopeCheckQuery(Guid TenantId, BindingScope Scope, IReadOnlyList<Guid> KnowledgeBaseIds, IReadOnlyList<Guid> FieldIds);

public sealed record ScopeCheck(IReadOnlyList<Guid> HiddenKnowledgeBaseIds, IReadOnlyList<Guid> HiddenFieldIds)
{
    public bool IsVisible => HiddenKnowledgeBaseIds.Count == 0 && HiddenFieldIds.Count == 0;
}

/// <param name="TenantId">The tenant of the binding; it must be the tenant of the current request.</param>
/// <param name="FieldIds">The fields a filter may name: the ones the binding's knowledge bases map.</param>
/// <param name="Filters">The fixed filters in the shape of the generated tool schema: <c>department</c>, <c>year_min</c>, <c>published_on_to</c>...</param>
public sealed record FixedFilterQuery(Guid TenantId, IReadOnlyList<Guid> FieldIds, IReadOnlyDictionary<string, JsonElement> Filters);

/// <param name="Problem">What is wrong with the filters, written for the administrator, or <see langword="null"/> when they can be used.</param>
/// <param name="FieldIds">The fields the filters name.</param>
public sealed record FixedFilterCheck(string? Problem, IReadOnlyList<Guid> FieldIds);
