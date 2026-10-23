using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features;

/// <summary>
/// The filters of the admin lists (spec 010), applied to this module's own queries so that one rule serves the three lists:
/// <c>scope</c> is the kind; <c>tenant</c> matches the owner or a tenant the item is shared with; <c>assistant</c> matches an
/// item scoped to it or any item that is not scoped to assistants (it applies to every assistant). Values of one filter are
/// alternatives; different filters all have to match.
/// </summary>
internal static class ScopeFilter
{
    public static Result<IQueryable<KnowledgeBase>> Apply(IQueryable<KnowledgeBase> source, ListQuery query)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        if (!TryParse<KnowledgeBaseScope>(query.Scope, "scope", out var scopes, out var error)
            || !TryParse<KnowledgeBaseKind>(query.Type, "type", out var kinds, out error)
            || !TryParse<KnowledgeBaseStatus>(query.Status, "status", out var statuses, out error))
        {
            return error;
        }

        var tenants = query.Tenant.ToList();
        var assistants = query.Assistant.ToList();

        if (scopes.Count > 0)
        {
            source = source.Where(k => scopes.Contains(k.Scope));
        }

        if (tenants.Count > 0)
        {
            source = source.Where(k => tenants.Contains(k.TenantId) || k.ScopeTenantIds.Any(t => tenants.Contains(t)));
        }

        if (assistants.Count > 0)
        {
            source = source.Where(k => k.Scope != KnowledgeBaseScope.Assistant || k.ScopeAssistantIds.Any(a => assistants.Contains(a)));
        }

        if (kinds.Count > 0)
        {
            source = source.Where(k => kinds.Contains(k.Kind));
        }

        if (statuses.Count > 0)
        {
            source = source.Where(k => statuses.Contains(k.Status));
        }

        if (Pattern(query.Q) is { } pattern)
        {
            source = source.Where(k => EF.Functions.ILike(k.Name, pattern, EscapeCharacter) || EF.Functions.ILike(k.Description, pattern, EscapeCharacter));
        }

        return Result<IQueryable<KnowledgeBase>>.Success(source);
    }

    /// <summary>Manual lookups have a kind of scope, an owner and the tenants they are shared with; no type and no status.</summary>
    public static Result<IQueryable<Lookup>> Apply(IQueryable<Lookup> source, ListQuery query)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        if (!TryParse<LookupScope>(query.Scope, "scope", out var scopes, out var error))
        {
            return error;
        }

        if (query.Type.Count + query.Status.Count > 0)
        {
            return Unsupported("The lookups have no type or status to filter by.");
        }

        var tenants = query.Tenant.ToList();

        if (scopes.Count > 0)
        {
            var nullable = scopes.Select(s => (LookupScope?)s).ToList();
            source = source.Where(l => nullable.Contains(l.Scope));
        }

        if (tenants.Count > 0)
        {
            source = source.Where(l => tenants.Contains(l.TenantId) || l.ScopeTenantIds.Any(t => tenants.Contains(t)));
        }

        if (Pattern(query.Q) is { } pattern)
        {
            source = source.Where(l => EF.Functions.ILike(l.Name!, pattern, EscapeCharacter) || (l.Description != null && EF.Functions.ILike(l.Description, pattern, EscapeCharacter)));
        }

        return Result<IQueryable<Lookup>>.Success(source);
    }

    /// <summary>The aggregate lookups list, where a derived lookup has the scope of its knowledge base; <c>kind</c> is filtered by the endpoint.</summary>
    public static Result<IQueryable<LookupRow>> Apply(IQueryable<LookupRow> source, ListQuery query)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        if (!TryParse<KnowledgeBaseScope>(query.Scope, "scope", out var scopes, out var error))
        {
            return error;
        }

        if (query.Type.Count + query.Status.Count > 0)
        {
            return Unsupported("The lookups have no type or status to filter by: use kind.");
        }

        var tenants = query.Tenant.ToList();
        var assistants = query.Assistant.ToList();

        if (scopes.Count > 0)
        {
            source = source.Where(r => scopes.Contains(r.Scope));
        }

        if (tenants.Count > 0)
        {
            source = source.Where(r => tenants.Contains(r.TenantId) || r.ScopeTenantIds.Any(t => tenants.Contains(t)));
        }

        if (assistants.Count > 0)
        {
            source = source.Where(r => r.Scope != KnowledgeBaseScope.Assistant || r.ScopeAssistantIds!.Any(a => assistants.Contains(a)));
        }

        if (Pattern(query.Q) is { } pattern)
        {
            source = source.Where(r => EF.Functions.ILike(r.Name, pattern, EscapeCharacter));
        }

        return Result<IQueryable<LookupRow>>.Success(source);
    }

    /// <summary>Fields are Global or Tenant, owned by one tenant and never shared with named ones; <c>type</c> is the field type.</summary>
    public static Result<IQueryable<DictionaryField>> Apply(IQueryable<DictionaryField> source, ListQuery query)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        if (!TryParse<DictionaryFieldScope>(query.Scope, "scope", out var scopes, out var error)
            || !TryParse<DictionaryFieldType>(query.Type, "type", out var types, out error))
        {
            return error;
        }

        if (query.Status.Count > 0)
        {
            return Unsupported("The dictionary fields have no status to filter by.");
        }

        var tenants = query.Tenant.ToList();

        if (scopes.Count > 0)
        {
            source = source.Where(f => scopes.Contains(f.Scope));
        }

        if (tenants.Count > 0)
        {
            source = source.Where(f => tenants.Contains(f.TenantId));
        }

        if (types.Count > 0)
        {
            source = source.Where(f => types.Contains(f.Type));
        }

        if (Pattern(query.Q) is { } pattern)
        {
            source = source.Where(f => EF.Functions.ILike(f.Name, pattern, EscapeCharacter) || (f.Description != null && EF.Functions.ILike(f.Description, pattern, EscapeCharacter)) || (f.Cluster != null && EF.Functions.ILike(f.Cluster, pattern, EscapeCharacter)));
        }

        return Result<IQueryable<DictionaryField>>.Success(source);
    }

    private const string EscapeCharacter = "\\";

    private static Error Unsupported(string description) => Error.Validation("list.invalid_filter", description);

    // The text is data, not a pattern: its wildcards are escaped.
    private static string? Pattern(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : "%" + text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static bool TryParse<TEnum>(IReadOnlyList<string> values, string filter, out List<TEnum> parsed, out Error error)
        where TEnum : struct, Enum
    {
        parsed = [];
        error = null!;
        foreach (var value in values)
        {
            if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var member) || !Enum.IsDefined(member))
            {
                error = Unsupported($"'{value}' is not a value of the {filter} filter: use {string.Join(", ", Enum.GetNames<TEnum>())}.");
                return false;
            }

            parsed.Add(member);
        }

        return true;
    }
}
