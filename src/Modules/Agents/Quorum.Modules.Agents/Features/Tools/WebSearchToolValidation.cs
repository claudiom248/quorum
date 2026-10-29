using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools.WebSearch;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Tools;

/// <param name="Policy">The policy asked for, or <see langword="null"/> when the request left it out.</param>
internal sealed record ValidatedWebSearchTool(string Name, string Description, ToolPolicy? Policy, WebSearchToolConfiguration Configuration);

/// <summary>
/// What an administrator may enter for a web search tool (spec 005, AC-1 and AC-7). Everything here is what SearXNG can answer with:
/// a value it does not know would be ignored by it, and the model would never learn that its filter did nothing.
/// </summary>
internal sealed partial class WebSearchToolValidation(AgentsDbContext db)
{
    public const int MaxNameLength = 40;

    public const int MaxDescriptionLength = 1_000;

    private static readonly Error InvalidName = Error.Validation(
        "web_search.invalid_name",
        $"The name must be 1 to {MaxNameLength} characters: lower-case letters, digits, '-' and '_', starting with a letter or digit.");

    private static readonly Error InvalidDescription = Error.Validation(
        "web_search.invalid_description",
        $"The description is required, up to {MaxDescriptionLength} characters: the model reads it to decide when to search the web.");

    private static readonly Error InvalidPolicy = Error.Validation("web_search.invalid_policy", "The policy must be Auto or Confirm.");

    private static readonly Error InvalidTopN = Error.Validation(
        "web_search.invalid_top_n",
        $"The number of results must be between {WebSearchTool.MinTopN} and {WebSearchTool.MaxTopN}.");

    private static readonly Error InvalidSafeSearch = Error.Validation(
        "web_search.invalid_safe_search",
        "The safe search level must be 0 (off), 1 (moderate) or 2 (strict).");

    private static readonly Error InvalidMaxInvocations = Error.Validation(
        "web_search.invalid_max_invocations",
        $"The most invocations per conversation must be between {WebSearchTool.MinMaxInvocationsPerConversation} and {WebSearchTool.MaxMaxInvocationsPerConversation}.");

    private static readonly Error InvalidTimeRange = Error.Validation(
        "web_search.invalid_time_range",
        $"The time range must be one of: {string.Join(", ", WebSearchTool.TimeRanges)}.");

    private static readonly Error InvalidLanguage = Error.Validation(
        "web_search.invalid_language",
        "The language must be a two-letter code, with an optional two-letter region: 'it', 'en', 'en-us'.");

    private static readonly Error InvalidSearchEngine = Error.Validation(
        "web_search.invalid_search_engine",
        $"Every engine must be one of the names the SearXNG instance uses: 1 to 32 characters of lower-case letters, digits, '_' and '-', starting with a letter or digit, up to {WebSearchTool.MaxSearchEngines} of them.");

    private static readonly Error NotConfigured = Error.Validation(
        "web_search.not_configured",
        "Web search configuration missing: an administrator has to set the SearXNG instance of the platform first.");

    public async Task<Result<ValidatedWebSearchTool>> ValidateAsync(WebSearchToolRequest request, Guid tenantId, CancellationToken cancellationToken)
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

        var topN = request.TopN ?? WebSearchTool.DefaultTopN;
        if (topN is < WebSearchTool.MinTopN or > WebSearchTool.MaxTopN)
        {
            return InvalidTopN;
        }

        var safeSearch = request.SafeSearch ?? WebSearchTool.DefaultSafeSearch;
        if (safeSearch is < 0 or > WebSearchTool.MaxSafeSearch)
        {
            return InvalidSafeSearch;
        }

        var maxInvocations = request.MaxInvocationsPerConversation ?? WebSearchTool.DefaultMaxInvocationsPerConversation;
        if (maxInvocations is < WebSearchTool.MinMaxInvocationsPerConversation or > WebSearchTool.MaxMaxInvocationsPerConversation)
        {
            return InvalidMaxInvocations;
        }

        var timeRange = Trimmed(request.TimeRange);
        if (timeRange is not null && !WebSearchTool.TimeRanges.Contains(timeRange, StringComparer.Ordinal))
        {
            return InvalidTimeRange;
        }

        var language = Trimmed(request.Language);
        if (language is not null)
        {
            language = language.ToLowerInvariant();
            if (!ValidLanguagePattern().IsMatch(language))
            {
                return InvalidLanguage;
            }
        }

        var engines = request.SearchEngines is null ? null : ParseEngines(request.SearchEngines);
        if (engines is { Count: 0 })
        {
            return InvalidSearchEngine;
        }

        var scope = await ToolScopeRules.ValidateAsync(request.Scope, db, cancellationToken);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        // The instance is the platform's, so a tool without one could only ever fail (AC-7).
        if (await db.WebSearchConfigurations.SingleOrDefaultAsync(cancellationToken) is not { Url: { Length: > 0 } })
        {
            return NotConfigured;
        }

        return new ValidatedWebSearchTool(
            name,
            description,
            policy,
            new WebSearchToolConfiguration(
                topN,
                engines,
                language,
                safeSearch,
                timeRange,
                maxInvocations,
                scope.Value.Kind,
                scope.Value.TenantIds,
                scope.Value.AssistantIds));
    }

    /// <summary>Distinct and in order: which engines an administrator listed is not part of what the tool is.</summary>
    private static List<string>? ParseEngines(List<string>? asked)
    {
        if (asked is null)
        {
            return null;
        }

        var engines = new List<string>();
        foreach (var engine in asked)
        {
            var name = engine?.Trim().ToLowerInvariant();
            if (name is null || !ValidEnginePattern().IsMatch(name))
            {
                return [];
            }

            if (!engines.Contains(name, StringComparer.Ordinal))
            {
                engines.Add(name);
            }
        }

        if (engines.Count > WebSearchTool.MaxSearchEngines)
        {
            return [];
        }

        engines.Sort(StringComparer.Ordinal);
        return engines;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex ValidNamePattern();

    [GeneratedRegex("^[a-z]{2}(-[a-z]{2})?$")]
    private static partial Regex ValidLanguagePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$")]
    private static partial Regex ValidEnginePattern();
}

/// <param name="SearchEngines">The engines to ask; leave it out to ask all of the ones the instance enables.</param>
/// <param name="TimeRange">One of <see cref="WebSearchTool.TimeRanges"/>, or nothing for no limit.</param>
/// <param name="MaxInvocationsPerConversation">How many times one conversation may call the tool.</param>
internal sealed record WebSearchToolRequest(
    string? Name,
    string? Description,
    int? TopN,
    List<string>? SearchEngines,
    string? Language,
    int? SafeSearch,
    string? TimeRange,
    int? MaxInvocationsPerConversation,
    ScopeRequest? Scope,
    string? Policy);
