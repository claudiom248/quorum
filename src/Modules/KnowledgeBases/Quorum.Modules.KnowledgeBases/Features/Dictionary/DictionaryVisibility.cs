using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Dictionary;

/// <summary>What the current tenant can see of the dictionary: its own fields, and the Global fields of any tenant.</summary>
internal static class DictionaryVisibility
{
    public static IQueryable<DictionaryField> Fields(KnowledgeBasesDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId ?? Guid.Empty;

        return db.DictionaryFields.IgnoreQueryFilters()
            .Where(field => field.TenantId == tenantId || field.Scope == DictionaryFieldScope.Global);
    }

    /// <summary>
    /// Whether a new field's name is already used where the field will be seen. A Global field is seen by every tenant, so every
    /// tenant's fields count; a private one is seen only by its owner.
    /// </summary>
    public static Task<bool> NameIsTakenAsync(KnowledgeBasesDbContext db, string name, DictionaryFieldScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var candidates = scope == DictionaryFieldScope.Global ? db.DictionaryFields.IgnoreQueryFilters() : Fields(db);
        return candidates.AnyAsync(field => field.Name == name, cancellationToken);
    }
}
