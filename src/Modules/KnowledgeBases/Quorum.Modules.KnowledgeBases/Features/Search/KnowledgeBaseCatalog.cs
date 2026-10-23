using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Dictionary;
using Quorum.Modules.KnowledgeBases.Features.Registry;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <summary>
/// <see cref="IKnowledgeBaseCatalog"/>. Like the search, it answers for the tenant of the request only: for any other it behaves as
/// if that tenant could see nothing, which also tells a caller nothing about what exists.
/// </summary>
internal sealed class KnowledgeBaseCatalog(KnowledgeBasesDbContext db, ITenantContext tenant) : IKnowledgeBaseCatalog
{
    public async Task<BindingDescription> DescribeAsync(BindingQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (tenant.TenantId != query.TenantId || query.TenantId == Guid.Empty)
        {
            return new BindingDescription([], []);
        }

        var ids = query.KnowledgeBaseIds.Distinct().ToList();
        var visible = query.AssistantId is { } assistantId ? KnowledgeBaseVisibility.ForAssistant(db, assistantId) : KnowledgeBaseVisibility.KnowledgeBases(db);
        var bases = await visible.AsNoTracking().Where(k => ids.Contains(k.Id)).OrderBy(k => k.Name).ToListAsync(cancellationToken);

        var wanted = bases.SelectMany(k => k.Mapping.Fields.Select(f => f.DictionaryFieldId)).Concat(query.FieldIds).Distinct().ToList();
        var fields = await DictionaryVisibility.Fields(db).AsNoTracking().Where(f => wanted.Contains(f.Id)).OrderBy(f => f.Name).ToListAsync(cancellationToken);
        var visibleFieldIds = fields.Select(f => f.Id).ToHashSet();

        return new BindingDescription(
            [.. bases.Select(k => new KnowledgeBaseInfo(
                k.Id,
                k.Name,
                k.Description,
                [.. k.Mapping.Fields.Select(f => f.DictionaryFieldId).Where(visibleFieldIds.Contains).Distinct()],
                k.Mapping.Roles.DocumentId is not null))],
            [.. fields.Select(f => new FilterFieldInfo(f.Id, f.Name, TypeOf(f.Type), f.Description, f.Cluster))]);
    }

    public async Task<ScopeCheck> CheckScopeAsync(ScopeCheckQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var knowledgeBaseIds = query.KnowledgeBaseIds.Distinct().ToList();
        var fieldIds = query.FieldIds.Distinct().ToList();
        if (tenant.TenantId != query.TenantId || query.TenantId == Guid.Empty)
        {
            return new ScopeCheck(knowledgeBaseIds, fieldIds);
        }

        // What the owner cannot see itself is hidden whatever the scope, and a missing one is hidden like any other.
        var bases = await KnowledgeBaseVisibility.KnowledgeBases(db).AsNoTracking().Where(k => knowledgeBaseIds.Contains(k.Id)).ToListAsync(cancellationToken);
        var fields = await DictionaryVisibility.Fields(db).AsNoTracking().Where(f => fieldIds.Contains(f.Id)).ToListAsync(cancellationToken);

        var seenBases = bases.Where(k => ScopeVisibility.KnowledgeBaseIsVisible(k, query.TenantId, query.Scope)).Select(k => k.Id).ToHashSet();
        var seenFields = fields.Where(f => ScopeVisibility.FieldIsVisible(f, query.TenantId, query.Scope)).Select(f => f.Id).ToHashSet();

        return new ScopeCheck(
            [.. knowledgeBaseIds.Where(id => !seenBases.Contains(id))],
            [.. fieldIds.Where(id => !seenFields.Contains(id))]);
    }

    public async Task<FixedFilterCheck> CheckFixedFiltersAsync(FixedFilterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (tenant.TenantId != query.TenantId || query.TenantId == Guid.Empty)
        {
            return new FixedFilterCheck("The filters cannot be checked.", []);
        }

        var aliases = query.Aliases ?? new Dictionary<string, Guid>();
        var ids = query.FieldIds.Concat(aliases.Values).Distinct().ToList();
        var fields = await DictionaryVisibility.Fields(db).AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(cancellationToken);
        var names = FixedFilterNames.Build(
            aliases.Where(pair => fields.Any(f => f.Id == pair.Value)).ToDictionary(pair => pair.Key, pair => fields.First(f => f.Id == pair.Value), StringComparer.Ordinal),
            fields.Where(f => query.FieldIds.Contains(f.Id)));
        if (names.AmbiguityProblem(query.Filters.Keys) is { } ambiguous)
        {
            return new FixedFilterCheck(ambiguous, []);
        }

        var byName = names.Fields;

        // A fixed filter that restricts nothing is a mistake that would widen access, so an empty value is refused, not ignored.
        var parsed = SearchFilterParser.Parse(query.Filters, byName, KnowledgeBaseSearchErrors.InvalidFixedFilter, ignoreEmpty: false);
        return parsed.IsSuccess
            ? new FixedFilterCheck(null, [.. parsed.Value.Select(f => f.Field.Id).Distinct()])
            : new FixedFilterCheck(parsed.Error.Description, []);
    }

    private static FilterFieldType TypeOf(DictionaryFieldType type) => type switch
    {
        DictionaryFieldType.Text => FilterFieldType.Text,
        DictionaryFieldType.Number => FilterFieldType.Number,
        DictionaryFieldType.Boolean => FilterFieldType.Boolean,
        DictionaryFieldType.Date => FilterFieldType.Date,
        DictionaryFieldType.TextList => FilterFieldType.TextList,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown field type."),
    };
}
