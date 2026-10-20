using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>What the admin types to say who can use a knowledge base or a rag tool; turned into the scope the API takes.</summary>
public sealed class ScopeForm
{
    public string Kind { get; set; } = ScopeKinds.Tenant;

    /// <summary>Tenants besides the owner, as text (scope <c>Tenant</c>).</summary>
    public string TenantsText { get; set; } = string.Empty;

    public HashSet<Guid> AssistantIds { get; init; } = [];

    public static ScopeForm From(ToolScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new ScopeForm
        {
            Kind = scope.Kind,
            TenantsText = string.Join(Environment.NewLine, scope.TenantIds),
            AssistantIds = [.. scope.AssistantIds],
        };
    }

    /// <summary>The scope to send, or what is wrong with what was typed. The server checks the ids again.</summary>
    public (ToolScope? Scope, string? Error) Build()
    {
        switch (Kind)
        {
            case ScopeKinds.Global:
                return (new ToolScope(ScopeKinds.Global, [], []), null);
            case ScopeKinds.Tenant:
                var (ids, error) = ToolAdminView.ParseIds(TenantsText);
                return error is null ? (new ToolScope(ScopeKinds.Tenant, ids, []), null) : (null, error);
            case ScopeKinds.Assistant:
                return AssistantIds.Count == 0
                    ? (null, "Choose at least one assistant, or another scope.")
                    : (new ToolScope(ScopeKinds.Assistant, [], [.. AssistantIds]), null);
            default:
                return (null, "Choose who can use it.");
        }
    }
}
