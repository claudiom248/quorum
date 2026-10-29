using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools.Mcp;
using Quorum.Modules.Agents.Tools.WebFetch;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>
/// Everything the platform sets once for the open web (specs 005 and 006): the SearXNG instance, the web fetch defaults, and the
/// identity, politeness and cache shared by every outbound call. One configuration for every tenant, edited by an administrator;
/// a web search tool cannot be saved while it has no URL. Reading it before anyone saved one answers with the defaults.
/// </summary>
internal sealed class WebSettingsEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings/web")
            .WithTags("Settings")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        settings.MapGet("/", GetAsync).WithName("GetWebConfiguration");
        settings.MapPut("/", UpdateAsync).WithName("UpdateWebConfiguration");
    }

    private static async Task<Ok<WebConfigurationDto>> GetAsync(AgentsDbContext db, CancellationToken cancellationToken)
    {
        var configuration = await db.WebConfigurations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return TypedResults.Ok(WebConfigurationDto.From(configuration));
    }

    private static async Task<Results<NoContent, ValidationProblem>> UpdateAsync(
        WebConfigurationDto request,
        AgentsDbContext db,
        TimeProvider clock,
        WebFetchCache webFetchCache,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var searchUrl = request.SearchUrl?.Trim();
        var userAgent = request.UserAgent!.Trim();
        var now = clock.GetUtcNow();
        var configuration = await db.WebConfigurations.SingleOrDefaultAsync(cancellationToken);
        if (configuration is null)
        {
            configuration = WebConfiguration.CreateDefault(now);
            db.WebConfigurations.Add(configuration);
        }

        configuration.Change(
            searchUrl,
            request.SearchTimeoutSeconds,
            request.FetchTimeoutSeconds,
            request.FetchMaxUrlsPerInvocation,
            request.FetchTokenBudgetPerPage,
            request.FetchTotalTokenBudget,
            userAgent,
            request.FetchDelayMs,
            request.RespectRobotsTxt,
            request.CacheEnabled,
            request.CacheTtlMinutes,
            now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            // The row is a singleton on a fixed key: two administrators saving the first one at once cannot both create it,
            // so the loser retries on the row that won.
            db.ChangeTracker.Clear();
            var existing = await db.WebConfigurations.SingleAsync(cancellationToken);
            existing.Change(
                searchUrl,
                request.SearchTimeoutSeconds,
                request.FetchTimeoutSeconds,
                request.FetchMaxUrlsPerInvocation,
                request.FetchTokenBudgetPerPage,
                request.FetchTotalTokenBudget,
                userAgent,
                request.FetchDelayMs,
                request.RespectRobotsTxt,
                request.CacheEnabled,
                request.CacheTtlMinutes,
                now);
            await db.SaveChangesAsync(cancellationToken);
        }

        // What the platform remembers was read under the policy being replaced: a page fetched while robots were respected, or
        // with the old identity, would keep being served for the rest of its lifetime (spec 006, AC-8 and AC-14).
        await webFetchCache.ClearAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static Dictionary<string, string[]> Validate(WebConfigurationDto request)
    {
        var errors = new Dictionary<string, string[]>();

        // The address is taken as the administrator wrote it, with no filtering of where it points (as for an MCP server): only
        // its shape is checked, so a key cannot hide in the credentials of the URL.
        if (!string.IsNullOrEmpty(request.SearchUrl) && !McpServerRules.TryParseUrl(request.SearchUrl, out _))
        {
            errors["searchUrl"] = [$"The URL must be an absolute http(s) address of at most {McpServerRules.MaxUrlLength} characters, without credentials."];
        }

        if (request.SearchTimeoutSeconds is < WebConfiguration.MinSearchTimeoutSeconds or > WebConfiguration.MaxSearchTimeoutSeconds)
        {
            errors["searchTimeoutSeconds"] = [$"The value must be between {WebConfiguration.MinSearchTimeoutSeconds} and {WebConfiguration.MaxSearchTimeoutSeconds}."];
        }

        if (request.FetchTimeoutSeconds is < WebConfiguration.MinFetchTimeoutSeconds or > WebConfiguration.MaxFetchTimeoutSeconds)
        {
            errors["fetchTimeoutSeconds"] = [$"The value must be between {WebConfiguration.MinFetchTimeoutSeconds} and {WebConfiguration.MaxFetchTimeoutSeconds}."];
        }

        if (request.FetchMaxUrlsPerInvocation is < WebConfiguration.MinFetchMaxUrlsPerInvocation or > WebConfiguration.MaxFetchMaxUrlsPerInvocation)
        {
            errors["fetchMaxUrlsPerInvocation"] = [$"The value must be between {WebConfiguration.MinFetchMaxUrlsPerInvocation} and {WebConfiguration.MaxFetchMaxUrlsPerInvocation}."];
        }

        if (request.FetchTokenBudgetPerPage is < WebConfiguration.MinFetchTokenBudgetPerPage or > WebConfiguration.MaxFetchTokenBudgetPerPage)
        {
            errors["fetchTokenBudgetPerPage"] = [$"The value must be between {WebConfiguration.MinFetchTokenBudgetPerPage} and {WebConfiguration.MaxFetchTokenBudgetPerPage}."];
        }

        if (request.FetchTotalTokenBudget is < WebConfiguration.MinFetchTotalTokenBudget or > WebConfiguration.MaxFetchTotalTokenBudget)
        {
            errors["fetchTotalTokenBudget"] = [$"The value must be between {WebConfiguration.MinFetchTotalTokenBudget} and {WebConfiguration.MaxFetchTotalTokenBudget}."];
        }

        if (string.IsNullOrWhiteSpace(request.UserAgent) || request.UserAgent.Trim().Length > WebConfiguration.MaxUserAgentLength)
        {
            errors["userAgent"] = [$"The User-Agent is required and at most {WebConfiguration.MaxUserAgentLength} characters."];
        }

        if (request.FetchDelayMs is < WebConfiguration.MinFetchDelayMs or > WebConfiguration.MaxFetchDelayMs)
        {
            errors["fetchDelayMs"] = [$"The delay must be between {WebConfiguration.MinFetchDelayMs} and {WebConfiguration.MaxFetchDelayMs} milliseconds."];
        }

        if (request.CacheTtlMinutes is < WebConfiguration.MinCacheTtlMinutes or > WebConfiguration.MaxCacheTtlMinutes)
        {
            errors["cacheTtlMinutes"] = [$"The cache lifetime must be between {WebConfiguration.MinCacheTtlMinutes} and {WebConfiguration.MaxCacheTtlMinutes} minutes."];
        }

        return errors;
    }
}

/// <param name="SearchUrl">Where SearXNG answers; <see langword="null"/> until an administrator has set one (spec 005).</param>
/// <param name="SearchTimeoutSeconds">How many seconds a search waits before it is called a failure.</param>
/// <param name="FetchTimeoutSeconds">How many seconds one URL fetch waits before it is called a failure (spec 006).</param>
/// <param name="FetchMaxUrlsPerInvocation">The most URLs one invocation of the web fetch tool may fetch.</param>
/// <param name="FetchTokenBudgetPerPage">How much of one page's text the model reads at most.</param>
/// <param name="FetchTotalTokenBudget">How much every page of one invocation costs the model together, at most.</param>
/// <param name="UserAgent">The identity the platform announces to every web site it asks.</param>
/// <param name="FetchDelayMs">How long one answer waits between fetching one URL and the next.</param>
/// <param name="RespectRobotsTxt">Whether the platform reads a site's robots.txt before it fetches it.</param>
/// <param name="CacheEnabled">Whether fetched pages are remembered for a while.</param>
/// <param name="CacheTtlMinutes">How long a fetched page is remembered.</param>
internal sealed record WebConfigurationDto(
    string? SearchUrl,
    int SearchTimeoutSeconds,
    int FetchTimeoutSeconds,
    int FetchMaxUrlsPerInvocation,
    int FetchTokenBudgetPerPage,
    int FetchTotalTokenBudget,
    string UserAgent,
    int FetchDelayMs,
    bool RespectRobotsTxt,
    bool CacheEnabled,
    int CacheTtlMinutes)
{
    public static WebConfigurationDto From(WebConfiguration? configuration) => new(
        configuration?.SearchUrl,
        configuration?.SearchTimeoutSeconds ?? WebConfiguration.DefaultSearchTimeoutSeconds,
        configuration?.FetchTimeoutSeconds ?? WebConfiguration.DefaultFetchTimeoutSeconds,
        configuration?.FetchMaxUrlsPerInvocation ?? WebConfiguration.DefaultFetchMaxUrlsPerInvocation,
        configuration?.FetchTokenBudgetPerPage ?? WebConfiguration.DefaultFetchTokenBudgetPerPage,
        configuration?.FetchTotalTokenBudget ?? WebConfiguration.DefaultFetchTotalTokenBudget,
        configuration?.UserAgent ?? WebConfiguration.DefaultUserAgent,
        configuration?.FetchDelayMs ?? WebConfiguration.DefaultFetchDelayMs,
        configuration?.RespectRobotsTxt ?? WebConfiguration.DefaultRespectRobotsTxt,
        configuration?.CacheEnabled ?? WebConfiguration.DefaultCacheEnabled,
        configuration?.CacheTtlMinutes ?? WebConfiguration.DefaultCacheTtlMinutes);
}
