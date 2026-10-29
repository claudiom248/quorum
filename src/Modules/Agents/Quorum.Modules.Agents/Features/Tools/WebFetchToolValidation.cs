using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools.WebFetch;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Tools;

/// <param name="Policy">The policy asked for, or <see langword="null"/> when the request left it out.</param>
internal sealed record ValidatedWebFetchTool(string Name, string Description, ToolPolicy? Policy, WebFetchToolConfiguration Configuration);

/// <summary>
/// What an administrator may enter for a web fetch tool (spec 006, AC-1). The tool carries its own budgets and limits, so it does
/// not need the platform to have anything set — a field left out of the request takes the platform's current default (AC-13).
/// </summary>
internal sealed partial class WebFetchToolValidation(AgentsDbContext db)
{
    public const int MaxNameLength = 40;

    public const int MaxDescriptionLength = 1_000;

    private static readonly Error InvalidName = Error.Validation(
        "web_fetch.invalid_name",
        $"The name must be 1 to {MaxNameLength} characters: lower-case letters, digits, '-' and '_', starting with a letter or digit.");

    private static readonly Error InvalidDescription = Error.Validation(
        "web_fetch.invalid_description",
        $"The description is required, up to {MaxDescriptionLength} characters: the model reads it to decide when to fetch instead of answering from a search snippet.");

    private static readonly Error InvalidPolicy = Error.Validation("web_fetch.invalid_policy", "The policy must be Auto or Confirm.");

    private static readonly Error InvalidTokenBudgetPerPage = Error.Validation(
        "web_fetch.invalid_token_budget_per_page",
        $"The per-page budget must be between {WebFetchTool.MinTokenBudgetPerPage} and {WebFetchTool.MaxTokenBudgetPerPage} tokens.");

    private static readonly Error InvalidTotalTokenBudget = Error.Validation(
        "web_fetch.invalid_total_token_budget",
        $"The total budget must be between {WebFetchTool.MinTotalTokenBudget} and {WebFetchTool.MaxTotalTokenBudget} tokens.");

    private static readonly Error InvalidMaxUrls = Error.Validation(
        "web_fetch.invalid_max_urls",
        $"The most URLs per call must be between {WebFetchTool.MinMaxUrlsPerInvocation} and {WebFetchTool.MaxMaxUrlsPerInvocation}.");

    private static readonly Error InvalidTimeout = Error.Validation(
        "web_fetch.invalid_timeout",
        $"The timeout must be between {WebFetchTool.MinTimeoutSeconds} and {WebFetchTool.MaxTimeoutSeconds} seconds.");

    public async Task<Result<ValidatedWebFetchTool>> ValidateAsync(WebFetchToolRequest request, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim();
        if (name is null || !ValidNamePattern().IsMatch(name))
        {
            return InvalidName;
        }

        var description = request.Description?.Trim();
        if (string.IsNullOrEmpty(description) || description.Length > MaxDescriptionLength)
        {
            return InvalidDescription;
        }

        ToolPolicy? policy = null;
        if (request.Policy is not null)
        {
            if (!Enum.TryParse<ToolPolicy>(request.Policy, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(request.Policy, out _))
            {
                return InvalidPolicy;
            }

            policy = parsed;
        }

        // A field the request left out takes the platform's current default: the Settings tabs (spec 006, AC-13) only reach a
        // tool through this row, and the range check applies to what the request said and to the default alike.
        var web = await db.WebConfigurations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);

        var tokenBudgetPerPage = request.TokenBudgetPerPage ?? web?.FetchTokenBudgetPerPage ?? WebConfiguration.DefaultFetchTokenBudgetPerPage;
        if (tokenBudgetPerPage is < WebFetchTool.MinTokenBudgetPerPage or > WebFetchTool.MaxTokenBudgetPerPage)
        {
            return InvalidTokenBudgetPerPage;
        }

        var totalTokenBudget = request.TotalTokenBudget ?? web?.FetchTotalTokenBudget ?? WebConfiguration.DefaultFetchTotalTokenBudget;
        if (totalTokenBudget is < WebFetchTool.MinTotalTokenBudget or > WebFetchTool.MaxTotalTokenBudget)
        {
            return InvalidTotalTokenBudget;
        }

        var maxUrls = request.MaxUrlsPerInvocation ?? web?.FetchMaxUrlsPerInvocation ?? WebConfiguration.DefaultFetchMaxUrlsPerInvocation;
        if (maxUrls is < WebFetchTool.MinMaxUrlsPerInvocation or > WebFetchTool.MaxMaxUrlsPerInvocation)
        {
            return InvalidMaxUrls;
        }

        var timeout = request.Timeout ?? web?.FetchTimeoutSeconds ?? WebConfiguration.DefaultFetchTimeoutSeconds;
        if (timeout is < WebFetchTool.MinTimeoutSeconds or > WebFetchTool.MaxTimeoutSeconds)
        {
            return InvalidTimeout;
        }

        var scope = await ToolScopeRules.ValidateAsync(request.Scope, db, cancellationToken);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        return new ValidatedWebFetchTool(
            name,
            description,
            policy,
            new WebFetchToolConfiguration(
                tokenBudgetPerPage,
                totalTokenBudget,
                maxUrls,
                timeout,
                scope.Value.Kind,
                scope.Value.TenantIds,
                scope.Value.AssistantIds));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex ValidNamePattern();
}

internal sealed record WebFetchToolRequest(
    string? Name,
    string? Description,
    int? TokenBudgetPerPage,
    int? TotalTokenBudget,
    int? MaxUrlsPerInvocation,
    int? Timeout,
    ScopeRequest? Scope,
    string? Policy);
