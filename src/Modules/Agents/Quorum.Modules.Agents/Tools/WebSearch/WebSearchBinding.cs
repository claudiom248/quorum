using Quorum.Modules.Agents.Domain;

namespace Quorum.Modules.Agents.Tools.WebSearch;

/// <summary>The configuration of a web search tool as one answer uses it.</summary>
internal sealed record WebSearchBinding(
    int TopN,
    IReadOnlyList<string>? SearchEngines,
    string? Language,
    int SafeSearch,
    string? TimeRange,
    int MaxInvocationsPerConversation)
{
    public static WebSearchBinding From(WebSearchTool row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new WebSearchBinding(
            row.TopN,
            row.SearchEngines is { Count: > 0 } ? [.. row.SearchEngines] : null,
            row.Language,
            row.SafeSearch,
            row.TimeRange,
            row.MaxInvocationsPerConversation);
    }
}
