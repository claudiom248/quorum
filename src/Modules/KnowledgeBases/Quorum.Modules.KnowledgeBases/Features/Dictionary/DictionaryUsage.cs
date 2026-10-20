using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Dictionary;

/// <summary>What uses a dictionary field: knowledge bases that map it and rag tools that expose it or filter on it. Every tenant's: a Global field is used by things the caller cannot see.</summary>
internal sealed class DictionaryUsage
{
    private readonly List<User> _users;

    private DictionaryUsage(List<User> users) => _users = users;

    public static async Task<DictionaryUsage> LoadAsync(KnowledgeBasesDbContext db, IRagToolUsage tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tools);

        // Only the name, the owner and the mapping JSON are read: not the connection, with its encrypted key.
        var registrations = await db.KnowledgeBases.IgnoreQueryFilters().AsNoTracking()
            .Select(k => new { k.Name, k.TenantId, k.Mapping })
            .ToListAsync(cancellationToken);
        var rag = await tools.ListAsync(cancellationToken);

        return new DictionaryUsage(
        [
            .. registrations.Select(k => new User(k.Name, k.TenantId, IsTool: false, k.Mapping.Fields.Select(f => f.DictionaryFieldId).ToHashSet())),
            .. rag.Select(t => new User(t.ToolName, t.TenantId, IsTool: true, t.FieldIds.ToHashSet())),
        ]);
    }

    public int CountFor(Guid fieldId) => _users.Count(u => u.FieldIds.Contains(fieldId));

    /// <summary>The names of the caller's own knowledge bases and rag tools that use the field, and how many others (of other tenants) do.</summary>
    public (List<string> KnowledgeBases, List<string> Tools, int Others) UsersOf(Guid fieldId, Guid? currentTenantId)
    {
        var users = _users.Where(u => u.FieldIds.Contains(fieldId)).ToList();
        var own = users.Where(u => u.TenantId == currentTenantId).ToList();
        return (
            [.. own.Where(u => !u.IsTool).Select(u => u.Name).Order(StringComparer.Ordinal)],
            [.. own.Where(u => u.IsTool).Select(u => u.Name).Order(StringComparer.Ordinal)],
            users.Count(u => u.TenantId != currentTenantId));
    }

    private sealed record User(string Name, Guid TenantId, bool IsTool, HashSet<Guid> FieldIds);
}
