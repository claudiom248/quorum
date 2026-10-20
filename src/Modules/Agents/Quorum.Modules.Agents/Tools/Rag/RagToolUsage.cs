using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools.Rag;

/// <summary><see cref="IRagToolUsage"/>: the fields every rag tool exposes or filters on, whichever tenant owns it.</summary>
internal sealed class RagToolUsage(AgentsDbContext db) : IRagToolUsage
{
    public async Task<IReadOnlyList<RagToolUse>> ListAsync(CancellationToken cancellationToken)
    {
        // Every tenant's tools, on purpose: a Global field is used by tools its caller cannot see, and the caller counts the others.
        var tools = await (
            from tool in db.Tools.IgnoreQueryFilters()
            join rag in db.RagTools.IgnoreQueryFilters() on tool.Id equals rag.ToolId
            select new { tool.TenantId, tool.Name, rag.ExposedFieldIds, rag.FixedFilterFieldIds })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. tools.Select(t => new RagToolUse(t.TenantId, t.Name, [.. t.ExposedFieldIds.Concat(t.FixedFilterFieldIds).Distinct()]))];
    }
}
