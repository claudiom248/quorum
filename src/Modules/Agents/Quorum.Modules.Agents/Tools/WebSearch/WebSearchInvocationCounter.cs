using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools.WebSearch;

/// <summary>
/// How many times one conversation has already called a web search tool, read from the audit log: the entry of a call is written
/// before it runs, so the count includes the call being made. A call the user refused never reached the instance and is not counted,
/// or the refusals alone would use up the budget. A conversation older than the retention the administrator set starts again from
/// nothing, which is the cost of keeping no counter of our own.
/// </summary>
internal sealed class WebSearchInvocationCounter(AgentsDbContext db)
{
    public async Task<int> CountAsync(Guid conversationId, string toolName, CancellationToken cancellationToken) =>
        await db.ToolAuditEntries.IgnoreQueryFilters()
            .CountAsync(a => a.ConversationId == conversationId
                && a.ServerName == ToolNaming.WebSearchAuditServer
                && a.ToolName == toolName
                && a.Outcome != ToolOutcome.Denied,
                cancellationToken);
}
