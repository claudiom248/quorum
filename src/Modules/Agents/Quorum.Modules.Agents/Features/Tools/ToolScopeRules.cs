using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>A scope as an administrator sent it, checked: servers and rag tools share these rules.</summary>
internal sealed record ValidScope(ToolScopeKind Kind, List<Guid> TenantIds, List<Guid> AssistantIds);

internal static class ToolScopeRules
{
    private static readonly Error ScopeNotSupported = Error.Validation(
        "tool.scope_not_supported",
        "The agent scope is not available yet: agents do not exist. Use global, tenant or assistant.");

    public static async Task<Result<ValidScope>> ValidateAsync(ScopeRequest? scope, AgentsDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (scope is null || !Enum.TryParse<ToolScopeKind>(scope.Kind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind) || int.TryParse(scope.Kind, out _))
        {
            return Error.Validation("tool.invalid_scope", "The scope kind must be Global, Tenant or Assistant.");
        }

        if (kind == ToolScopeKind.Agent)
        {
            return ScopeNotSupported;
        }

        var tenantIds = (scope.TenantIds ?? []).Distinct().ToList();
        var assistantIds = (scope.AssistantIds ?? []).Distinct().ToList();
        if (Problem(kind, tenantIds, assistantIds) is { } problem)
        {
            return Error.Validation("tool.invalid_scope", problem);
        }

        if (kind == ToolScopeKind.Assistant
            && await db.Assistants.CountAsync(a => assistantIds.Contains(a.Id), cancellationToken) != assistantIds.Count)
        {
            return Error.Validation("tool.invalid_scope", "Every assistant in the scope must exist.");
        }

        return new ValidScope(kind, tenantIds, assistantIds);
    }

    /// <summary>The scope kind as the knowledge bases module knows it, to check what a binding of that scope can use.</summary>
    public static BindingScopeKind KindOf(ToolScopeKind kind) => kind switch
    {
        ToolScopeKind.Global => BindingScopeKind.Global,
        ToolScopeKind.Tenant => BindingScopeKind.Tenant,
        ToolScopeKind.Assistant => BindingScopeKind.Assistant,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Global, Tenant and Assistant scopes are validated."),
    };

    private static string? Problem(ToolScopeKind kind, List<Guid> tenantIds, List<Guid> assistantIds) => kind switch
    {
        ToolScopeKind.Global when tenantIds.Count > 0 || assistantIds.Count > 0 => "A global scope has no tenants or assistants.",
        ToolScopeKind.Tenant when assistantIds.Count > 0 => "A tenant scope lists tenants, not assistants.",
        ToolScopeKind.Assistant when tenantIds.Count > 0 => "An assistant scope lists assistants, not tenants.",
        ToolScopeKind.Assistant when assistantIds.Count == 0 => "An assistant scope needs at least one assistant.",
        _ when tenantIds.Contains(Guid.Empty) || assistantIds.Contains(Guid.Empty) => "An id in the scope is empty.",
        _ => null,
    };
}
