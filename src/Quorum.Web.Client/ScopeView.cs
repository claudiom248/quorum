using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>How a scope reads in a badge, the same in every list (spec 010, D-6).</summary>
public static class ScopeView
{
    /// <summary>Above this many assistants the badge shows a count instead of the names.</summary>
    public const int NamedAssistantsLimit = 2;

    public static string Label(ToolScope scope, IReadOnlyList<AssistantSummary> assistants)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(assistants);

        switch (scope.Kind)
        {
            case ScopeKinds.Global:
                return "Global";
            case ScopeKinds.Tenant:
                return scope.TenantIds.Count == 0 ? "Tenant" : $"Tenant +{scope.TenantIds.Count}";
            case ScopeKinds.Assistant:
                if (scope.AssistantIds.Count > NamedAssistantsLimit)
                {
                    return $"{scope.AssistantIds.Count} assistants";
                }

                var names = scope.AssistantIds.Select(id => assistants.FirstOrDefault(a => a.Id == id)?.Name ?? "an assistant you cannot see");
                return $"Assistants: {string.Join(", ", names)}";
            default:
                return scope.Kind;
        }
    }

    /// <summary>The full sentence, for the tooltip of a badge that had to be short.</summary>
    public static string Description(ToolScope scope, IReadOnlyList<AssistantSummary> assistants) =>
        ToolAdminView.ScopeSummary(scope, assistants);
}
