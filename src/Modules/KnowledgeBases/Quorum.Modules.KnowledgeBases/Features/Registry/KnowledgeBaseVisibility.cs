using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Registry;

/// <summary>
/// What the current tenant can see of the knowledge bases: its own, the Global ones of any tenant, and the Tenant ones that list it.
/// An Assistant scope is only ever the owner's: it names the owner's assistants.
/// </summary>
internal static class KnowledgeBaseVisibility
{
    public static IQueryable<KnowledgeBase> KnowledgeBases(KnowledgeBasesDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return db.KnowledgeBases.IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId
                        || k.Scope == KnowledgeBaseScope.Global
                        || (k.Scope == KnowledgeBaseScope.Tenant && k.ScopeTenantIds.Contains(tenantId)));
    }

    /// <summary>
    /// What one assistant of the current tenant can use: as <see cref="KnowledgeBases"/>, except that a knowledge base with the
    /// Assistant scope is only for the assistants it lists.
    /// </summary>
    public static IQueryable<KnowledgeBase> ForAssistant(KnowledgeBasesDbContext db, Guid assistantId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return db.KnowledgeBases.IgnoreQueryFilters()
            .Where(k => (k.TenantId == tenantId && (k.Scope != KnowledgeBaseScope.Assistant || k.ScopeAssistantIds.Contains(assistantId)))
                        || k.Scope == KnowledgeBaseScope.Global
                        || (k.Scope == KnowledgeBaseScope.Tenant && k.ScopeTenantIds.Contains(tenantId)));
    }
}
