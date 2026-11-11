namespace Quorum.Web.Client.Api;

/// <summary>A model endpoint as the admin sees it. The key itself is never returned.</summary>
public sealed record ModelEndpointInfo(Guid Id, string Name, string BaseUrl, string ModelName, bool HasApiKey, int ContextTokenBudget, bool SupportsTools);

/// <param name="ApiKey"><see langword="null"/> keeps the stored key, an empty string removes it.</param>
public sealed record ModelEndpointRequest(string Name, string BaseUrl, string ModelName, string? ApiKey, int ContextTokenBudget, bool SupportsTools);

public sealed record AgentListItem(
    Guid Id,
    string Slug,
    string DisplayName,
    string Description,
    string Scope,
    Guid? ModelEndpointId,
    int SubAgentCount,
    int ToolCount);

public static class FailurePolicies
{
    public const string ReturnError = "ReturnError";
    public const string AbortTurn = "AbortTurn";
}

public sealed record SubAgentEdge(string Slug, string FailurePolicy);

/// <summary>The agent as the API reads and writes it; sub-agents are referenced by slug and replaced as a whole.</summary>
public sealed record AgentDocument(
    Guid Id,
    string Slug,
    string DisplayName,
    string Description,
    string SystemPrompt,
    Guid? ModelEndpointId,
    string Scope,
    IReadOnlyList<Guid> ScopeTenantIds,
    IReadOnlyList<SubAgentEdge> SubAgents);

public sealed record AgentRequest(
    string Slug,
    string DisplayName,
    string Description,
    string SystemPrompt,
    Guid? ModelEndpointId,
    string Scope,
    IReadOnlyList<Guid> ScopeTenantIds,
    IReadOnlyList<SubAgentEdge> SubAgents);

public sealed record AssistantInfo(Guid Id, string Name, string RootAgentSlug, int MaxParallelSubAgents, bool Exposed = false, bool AllowToolSelection = false);

public sealed record AssistantRequest(string Name, string RootAgentSlug, int MaxParallelSubAgents, bool? Exposed = null, bool? AllowToolSelection = null);
