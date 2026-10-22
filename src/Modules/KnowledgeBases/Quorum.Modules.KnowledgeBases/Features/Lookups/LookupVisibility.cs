using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>What the current tenant can see of the lookups (spec 007 D-16).</summary>
internal static class LookupVisibility
{
    /// <summary>
    /// The manual lookups: the tenant's own, the Global ones of any tenant, and the Tenant ones that list it. Only the owner's can be changed.
    /// </summary>
    public static IQueryable<Lookup> Manual(KnowledgeBasesDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return db.Lookups.IgnoreQueryFilters()
            .Where(l => l.Kind == LookupKind.Manual
                        && (l.TenantId == tenantId
                            || l.Scope == LookupScope.Global
                            || (l.Scope == LookupScope.Tenant && l.ScopeTenantIds.Contains(tenantId))));
    }

    /// <summary>
    /// What the administrator can read the values of: every lookup of the tenant, whatever its kind, and the manual lookups others share.
    /// The lookups of a knowledge base another tenant shares are not here: they reach a tenant only through a tool.
    /// </summary>
    public static IQueryable<Lookup> Readable(KnowledgeBasesDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;
        var visibleManual = Manual(db).Select(l => l.Id);

        return db.Lookups.IgnoreQueryFilters().Where(l => l.TenantId == tenantId || visibleManual.Contains(l.Id));
    }
}
