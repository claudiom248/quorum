using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>A scope as an administrator sent it, checked: servers and tools share these rules.</summary>
/// <param name="BindingAssistantIds">
/// The assistants whose root agent is in <paramref name="AgentIds"/>: what a knowledge base scope can name (spec 013 D-41), so a binding
/// is checked against the knowledge bases those assistants see.
/// </param>
internal sealed record ValidScope(ToolScopeKind Kind, List<Guid> TenantIds, List<Guid> AgentIds, List<Guid> BindingAssistantIds);

internal static class ToolScopeRules
{
    public static async Task<Result<ValidScope>> ValidateAsync(ScopeRequest? scope, AgentsDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (scope is null || !Enum.TryParse<ToolScopeKind>(scope.Kind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind) || int.TryParse(scope.Kind, out _))
        {
            return Error.Validation("tool.invalid_scope", "The scope kind must be Global, Tenant or Agent.");
        }

        var tenantIds = (scope.TenantIds ?? []).Distinct().ToList();
        var agentIds = (scope.AgentIds ?? []).Distinct().ToList();
        if (Problem(kind, tenantIds, agentIds) is { } problem)
        {
            return Error.Validation("tool.invalid_scope", problem);
        }

        if (kind != ToolScopeKind.Agent)
        {
            return new ValidScope(kind, tenantIds, agentIds, []);
        }

        if (await db.Agents.CountAsync(a => agentIds.Contains(a.Id), cancellationToken) != agentIds.Count)
        {
            return Error.Validation("tool.invalid_scope", "Every agent in the scope must exist.");
        }

        var assistantIds = await db.Assistants.Where(a => agentIds.Contains(a.RootAgentId)).Select(a => a.Id).ToListAsync(cancellationToken);
        return new ValidScope(kind, tenantIds, agentIds, assistantIds);
    }

    /// <summary>The scope kind as the knowledge bases module knows it, to check what a binding of that scope can use.</summary>
    public static BindingScopeKind KindOf(ToolScopeKind kind) => kind switch
    {
        ToolScopeKind.Global => BindingScopeKind.Global,
        ToolScopeKind.Tenant => BindingScopeKind.Tenant,
        ToolScopeKind.Agent => BindingScopeKind.Assistant,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown scope kind."),
    };

    private static string? Problem(ToolScopeKind kind, List<Guid> tenantIds, List<Guid> agentIds) => kind switch
    {
        ToolScopeKind.Global when tenantIds.Count > 0 || agentIds.Count > 0 => "A global scope has no tenants or agents.",
        ToolScopeKind.Tenant when agentIds.Count > 0 => "A tenant scope lists tenants, not agents.",
        ToolScopeKind.Agent when tenantIds.Count > 0 => "An agent scope lists agents, not tenants.",
        ToolScopeKind.Agent when agentIds.Count == 0 => "An agent scope needs at least one agent.",
        _ when tenantIds.Contains(Guid.Empty) || agentIds.Contains(Guid.Empty) => "An id in the scope is empty.",
        _ => null,
    };
}
