using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools.Lookup;

/// <summary><see cref="ILookupToolUsage"/>: the lookups, fields and attributes every lookup tool uses, whichever tenant owns it.</summary>
internal sealed class LookupToolUsage(AgentsDbContext db) : ILookupToolUsage
{
    public async Task<IReadOnlyList<LookupToolUse>> ListAsync(CancellationToken cancellationToken)
    {
        // Every tenant's tools, on purpose: a Global lookup or field is used by tools its caller cannot see, and the caller counts the others.
        var tools = await (
            from tool in db.Tools.IgnoreQueryFilters()
            join lookup in db.LookupTools.IgnoreQueryFilters() on tool.Id equals lookup.ToolId
            select new { tool.TenantId, tool.Name, lookup.LookupIds, lookup.ExposedFieldIds, lookup.FixedFilterFieldIds, lookup.LookupFilters })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. tools.Select(t => new LookupToolUse(
            t.TenantId,
            t.Name,
            [.. t.LookupIds],
            [.. t.ExposedFieldIds.Concat(t.FixedFilterFieldIds).Distinct()],
            AttributeKeysOf(t.LookupFilters)))];
    }

    /// <summary>The attributes each lookup's fixed conditions name, read from the stored JSON.</summary>
    private static Dictionary<Guid, IReadOnlyList<string>> AttributeKeysOf(string lookupFilters)
    {
        using var document = JsonDocument.Parse(lookupFilters);
        return document.RootElement.EnumerateObject()
            .Where(p => Guid.TryParse(p.Name, out _) && p.Value.ValueKind == JsonValueKind.Object)
            .ToDictionary(p => Guid.Parse(p.Name), p => (IReadOnlyList<string>)[.. p.Value.EnumerateObject().Select(a => a.Name)]);
    }
}
