using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Dictionary;

/// <summary>What uses a dictionary field: knowledge bases that map it and rag and lookup tools that expose it or filter on it. Every tenant's: a Global field is used by things the caller cannot see.</summary>
internal sealed class DictionaryUsage
{
    private readonly List<User> _users;

    private DictionaryUsage(List<User> users) => _users = users;

    public static async Task<DictionaryUsage> LoadAsync(KnowledgeBasesDbContext db, IRagToolUsage ragTools, ILookupToolUsage lookupTools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ragTools);
        ArgumentNullException.ThrowIfNull(lookupTools);

        // Only the name, the owner and the mapping JSON are read: not the connection, with its encrypted key.
        var registrations = await db.KnowledgeBases.IgnoreQueryFilters().AsNoTracking()
            .Select(k => new { k.Name, k.TenantId, k.Mapping })
            .ToListAsync(cancellationToken);
        var rag = await ragTools.ListAsync(cancellationToken);
        var lookups = await lookupTools.ListAsync(cancellationToken);

        return new DictionaryUsage(
        [
            .. registrations.Select(k => new User(k.Name, k.TenantId, UserKind.KnowledgeBase, k.Mapping.Fields.Select(f => f.DictionaryFieldId).ToHashSet())),
            .. rag.Select(t => new User(t.ToolName, t.TenantId, UserKind.RagTool, t.FieldIds.ToHashSet())),
            .. lookups.Select(t => new User(t.ToolName, t.TenantId, UserKind.LookupTool, t.FieldIds.ToHashSet())),
        ]);
    }

    public int CountFor(Guid fieldId) => _users.Count(u => u.FieldIds.Contains(fieldId));

    /// <summary>The names of the caller's own knowledge bases, rag tools and lookup tools that use the field, and how many others (of other tenants) do.</summary>
    public (List<string> KnowledgeBases, List<string> RagTools, List<string> LookupTools, int Others) UsersOf(Guid fieldId, Guid? currentTenantId)
    {
        var users = _users.Where(u => u.FieldIds.Contains(fieldId)).ToList();
        var own = users.Where(u => u.TenantId == currentTenantId).ToList();

        List<string> NamesOf(UserKind kind) => [.. own.Where(u => u.Kind == kind).Select(u => u.Name).Order(StringComparer.Ordinal)];

        return (NamesOf(UserKind.KnowledgeBase), NamesOf(UserKind.RagTool), NamesOf(UserKind.LookupTool), users.Count(u => u.TenantId != currentTenantId));
    }

    private enum UserKind
    {
        KnowledgeBase,
        RagTool,
        LookupTool,
    }

    private sealed record User(string Name, Guid TenantId, UserKind Kind, HashSet<Guid> FieldIds);
}
