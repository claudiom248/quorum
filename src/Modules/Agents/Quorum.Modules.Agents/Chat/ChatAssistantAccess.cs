using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Modules.Agents.Chat;

/// <summary>The answers Conversations gets about assistants: exposure, selectable tools and endpoint overrides (spec 018 D-7, D-11, D-16).</summary>
internal sealed class ChatAssistantAccess(AgentsDbContext db) : IChatAssistantAccess
{
    public async Task<ChatSelectionCheck> CheckSelectionAsync(Guid assistantId, bool debug, IReadOnlyList<string> disabledTools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disabledTools);

        var assistant = await db.Assistants.AsNoTracking()
            .Where(a => a.Id == assistantId && (debug || a.Exposed))
            .Select(a => new { a.RootAgentId, a.AllowToolSelection })
            .SingleOrDefaultAsync(cancellationToken);
        if (assistant is null)
        {
            return new ChatSelectionCheck(ChatSelectionProblem.NotFound, []);
        }

        var names = disabledTools.Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return new ChatSelectionCheck(ChatSelectionProblem.None, []);
        }

        // A consumer may not choose tools of an assistant that hides them from consumers (D-2).
        if (!debug && !assistant.AllowToolSelection)
        {
            return new ChatSelectionCheck(ChatSelectionProblem.SelectionNotAllowed, names);
        }

        // Only attached tools of the root agent are selectable (D-5, D-7); a consumer may turn off optional ones only (D-4).
        var root = await AgentToolLoader.LoadAsync(db, assistant.RootAgentId, cancellationToken);
        var invalid = names
            .Where(name => root.FirstOrDefault(tool => tool.ExposedName == name) is not { } tool || (!debug && tool.Mode == ToolMode.Required))
            .ToList();

        return invalid.Count == 0
            ? new ChatSelectionCheck(ChatSelectionProblem.None, [])
            : new ChatSelectionCheck(ChatSelectionProblem.InvalidNames, invalid);
    }

    public async Task<ChatOverrideCheck> CheckOverrideAsync(Guid assistantId, Guid modelEndpointId, CancellationToken cancellationToken)
    {
        var endpoint = await db.ModelEndpoints.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == modelEndpointId && e.TenantId == db.CurrentTenantId)
            .Select(e => new { e.SupportsTools })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return new ChatOverrideCheck(ChatOverrideProblem.EndpointNotFound);
        }

        if (endpoint.SupportsTools)
        {
            return new ChatOverrideCheck(ChatOverrideProblem.None);
        }

        var rootAgentId = await db.Assistants.AsNoTracking().Where(a => a.Id == assistantId).Select(a => a.RootAgentId).SingleOrDefaultAsync(cancellationToken);
        var hasTools = await db.AgentTools.AsNoTracking().AnyAsync(t => t.AgentId == rootAgentId, cancellationToken)
            || await db.AgentSubAgents.AsNoTracking().AnyAsync(s => s.ParentAgentId == rootAgentId, cancellationToken);

        return new ChatOverrideCheck(hasTools ? ChatOverrideProblem.WithoutTools : ChatOverrideProblem.None);
    }
}
