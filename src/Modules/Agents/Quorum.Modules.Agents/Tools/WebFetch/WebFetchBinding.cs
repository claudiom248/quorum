using Quorum.Modules.Agents.Domain;

namespace Quorum.Modules.Agents.Tools.WebFetch;

/// <summary>
/// The configuration of a web fetch tool as one answer uses it: the numbers a call works with, with the tool's own overrides
/// already resolved against the platform's row — which is why they are read again for every answer and an administrator changing
/// the Settings tabs reaches the tools that never overrode that field (spec 006, AC-1, AC-13).
/// </summary>
internal sealed record WebFetchBinding(
    int TokenBudgetPerPage,
    int TotalTokenBudget,
    int MaxUrlsPerInvocation,
    int Timeout)
{
    /// <param name="row">What the tool says about itself; a null is a field left to the platform.</param>
    /// <param name="web">The platform's row, or <see langword="null"/> when nobody has saved it yet — its own defaults then apply.</param>
    public static WebFetchBinding From(WebFetchTool row, WebConfiguration? web)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new WebFetchBinding(
            row.TokenBudgetPerPage ?? web?.FetchTokenBudgetPerPage ?? WebConfiguration.DefaultFetchTokenBudgetPerPage,
            row.TotalTokenBudget ?? web?.FetchTotalTokenBudget ?? WebConfiguration.DefaultFetchTotalTokenBudget,
            row.MaxUrlsPerInvocation ?? web?.FetchMaxUrlsPerInvocation ?? WebConfiguration.DefaultFetchMaxUrlsPerInvocation,
            row.Timeout ?? web?.FetchTimeoutSeconds ?? WebConfiguration.DefaultFetchTimeoutSeconds);
    }
}
