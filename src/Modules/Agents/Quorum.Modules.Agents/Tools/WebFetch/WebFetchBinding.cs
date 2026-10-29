using Quorum.Modules.Agents.Domain;

namespace Quorum.Modules.Agents.Tools.WebFetch;

/// <summary>The configuration of a web fetch tool as one answer uses it.</summary>
internal sealed record WebFetchBinding(
    int TokenBudgetPerPage,
    int TotalTokenBudget,
    int MaxUrlsPerInvocation,
    int Timeout)
{
    public static WebFetchBinding From(WebFetchTool row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new WebFetchBinding(row.TokenBudgetPerPage, row.TotalTokenBudget, row.MaxUrlsPerInvocation, row.Timeout);
    }
}
