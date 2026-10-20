using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Dictionary;

/// <summary>Which knowledge bases map a dictionary field. Every tenant's count: a Global field is used by knowledge bases the caller cannot see.</summary>
internal sealed class DictionaryUsage
{
    private readonly List<(string Name, Guid TenantId, HashSet<Guid> FieldIds)> _users;

    private DictionaryUsage(List<(string Name, Guid TenantId, HashSet<Guid> FieldIds)> users) => _users = users;

    public static async Task<DictionaryUsage> LoadAsync(KnowledgeBasesDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Only the name, the owner and the mapping JSON are read: not the connection, with its encrypted key.
        var registrations = await db.KnowledgeBases.IgnoreQueryFilters().AsNoTracking()
            .Select(k => new { k.Name, k.TenantId, k.Mapping })
            .ToListAsync(cancellationToken);
        return new DictionaryUsage([.. registrations.Select(k => (k.Name, k.TenantId, k.Mapping.Fields.Select(f => f.DictionaryFieldId).ToHashSet()))]);
    }

    public int CountFor(Guid fieldId) => _users.Count(u => u.FieldIds.Contains(fieldId));

    /// <summary>The names of the caller's own knowledge bases that use the field, and how many others (of other tenants) do.</summary>
    public (List<string> Own, int Others) UsersOf(Guid fieldId, Guid? currentTenantId)
    {
        var users = _users.Where(u => u.FieldIds.Contains(fieldId)).ToList();
        return ([.. users.Where(u => u.TenantId == currentTenantId).Select(u => u.Name).Order(StringComparer.Ordinal)], users.Count(u => u.TenantId != currentTenantId));
    }
}
