using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools.Mcp;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>
/// The SearXNG instance of the platform (spec 005, AC-9). One configuration for every tenant, edited by an administrator; a web
/// search tool cannot be saved while it has no URL. Reading it before anyone saved one answers with the defaults.
/// </summary>
internal sealed class WebSearchSettingsEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings/web-search")
            .WithTags("Settings")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        settings.MapGet("/", GetAsync).WithName("GetWebSearchConfiguration");
        settings.MapPut("/", UpdateAsync).WithName("UpdateWebSearchConfiguration");
    }

    private static async Task<Ok<WebSearchConfigurationDto>> GetAsync(AgentsDbContext db, CancellationToken cancellationToken)
    {
        var configuration = await db.WebSearchConfigurations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return TypedResults.Ok(WebSearchConfigurationDto.From(configuration));
    }

    private static async Task<Results<NoContent, ValidationProblem>> UpdateAsync(
        WebSearchConfigurationDto request,
        AgentsDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var url = request.Url!.Trim();
        var now = clock.GetUtcNow();
        var configuration = await db.WebSearchConfigurations.SingleOrDefaultAsync(cancellationToken);
        if (configuration is null)
        {
            configuration = WebSearchConfiguration.Create(url, request.DefaultTimeout, now);
            db.WebSearchConfigurations.Add(configuration);
        }
        else
        {
            configuration.Change(url, request.DefaultTimeout, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static Dictionary<string, string[]> Validate(WebSearchConfigurationDto request)
    {
        var errors = new Dictionary<string, string[]>();

        // The address is taken as the administrator wrote it, with no filtering of where it points (as for an MCP server): only its
        // shape is checked, so a key cannot hide in the credentials of the URL.
        if (!McpServerRules.TryParseUrl(request.Url, out _))
        {
            errors["url"] = [$"The URL must be an absolute http(s) address of at most {McpServerRules.MaxUrlLength} characters, without credentials."];
        }

        if (request.DefaultTimeout is < WebSearchConfiguration.MinTimeoutSeconds or > WebSearchConfiguration.MaxTimeoutSeconds)
        {
            errors["defaultTimeout"] = [$"The value must be between {WebSearchConfiguration.MinTimeoutSeconds} and {WebSearchConfiguration.MaxTimeoutSeconds}."];
        }

        return errors;
    }
}

/// <param name="Url">Where SearXNG answers; <see langword="null"/> until an administrator has set one.</param>
/// <param name="DefaultTimeout">How many seconds a search waits before it is called a failure.</param>
internal sealed record WebSearchConfigurationDto(string? Url, int DefaultTimeout)
{
    public static WebSearchConfigurationDto From(WebSearchConfiguration? configuration) => new(
        configuration?.Url,
        configuration?.TimeoutSeconds ?? WebSearchConfiguration.DefaultTimeoutSeconds);
}
