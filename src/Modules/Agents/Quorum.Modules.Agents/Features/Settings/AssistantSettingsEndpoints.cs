using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Secrets;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Settings;

/// <summary>The default assistant's model and system prompt, editable by the administrator after the setup.</summary>
internal sealed class AssistantSettingsEndpoints : IEndpointGroup
{
    private static readonly Error NotConfigured = Error.NotFound("assistant.not_configured", "The default assistant is not configured.");

    public void Map(IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings/assistant")
            .WithTags("Settings")
            .RequireAuthorization(QuorumPolicies.Admin);

        settings.MapGet("/", GetAsync).WithName("GetAssistantSettings");
        settings.MapPut("/", UpdateAsync).WithName("UpdateAssistantSettings");
        settings.MapPost("/test", TestConnectionAsync).WithName("TestAssistantModelConnection");
    }

    private static async Task<Results<Ok<AssistantSettingsResponse>, ProblemHttpResult>> GetAsync(AgentsDbContext db, CancellationToken cancellationToken)
    {
        var settings = await (
            from assistant in db.Assistants
            join endpoint in db.ModelEndpoints on assistant.ModelEndpointId equals endpoint.Id
            orderby assistant.Id
            select new AssistantSettingsResponse(endpoint.BaseUrl, endpoint.ModelName, endpoint.ProtectedApiKey != null, endpoint.ContextTokenBudget, assistant.SystemPrompt))
            .FirstOrDefaultAsync(cancellationToken);

        return settings is null ? NotConfigured.ToProblem() : TypedResults.Ok(settings);
    }

    /// <summary>
    /// <c>apiKey</c>: omitted or <see langword="null"/> keeps the stored key, an empty string removes it.
    /// </summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        UpdateAssistantSettingsRequest request,
        AgentsDbContext db,
        ApiKeyProtector apiKeyProtector,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request, out var baseUrl);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var assistant = await db.Assistants.OrderBy(a => a.Id).FirstOrDefaultAsync(cancellationToken);
        var endpoint = assistant is null ? null : await db.ModelEndpoints.SingleOrDefaultAsync(e => e.Id == assistant.ModelEndpointId, cancellationToken);
        if (assistant is null || endpoint is null)
        {
            return NotConfigured.ToProblem();
        }

        var now = clock.GetUtcNow();
        endpoint.Configure(baseUrl!, request.ModelName!.Trim(), request.ContextTokenBudget, now);
        if (request.ApiKey is not null)
        {
            endpoint.ReplaceApiKey(apiKeyProtector.Protect(request.ApiKey.Trim()), now);
        }

        assistant.ChangeSystemPrompt(string.IsNullOrWhiteSpace(request.SystemPrompt) ? AssistantDefaults.SystemPrompt : request.SystemPrompt.Trim());
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>Without an API key in the request, the stored key is used.</summary>
    private static async Task<Results<Ok<ModelConnectionResponse>, ValidationProblem, ProblemHttpResult>> TestConnectionAsync(
        TestConnectionRequest request,
        AgentsDbContext db,
        ApiKeyProtector apiKeyProtector,
        IModelEndpointProbe probe,
        CancellationToken cancellationToken)
    {
        if (!ModelEndpointRules.TryParseBaseUrl(request.BaseUrl, out var baseUrl))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = [ModelEndpointRules.InvalidBaseUrl] });
        }

        var apiKey = request.ApiKey;
        if (apiKey is null)
        {
            var stored = await (
                from assistant in db.Assistants
                join endpoint in db.ModelEndpoints on assistant.ModelEndpointId equals endpoint.Id
                orderby assistant.Id
                select endpoint.ProtectedApiKey)
                .FirstOrDefaultAsync(cancellationToken);
            apiKey = apiKeyProtector.Unprotect(stored);
        }

        var result = await probe.ProbeAsync(baseUrl, apiKey, cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(new ModelConnectionResponse(Reachable: true, result.Value.Models))
            : result.Error.ToProblem();
    }

    private static Dictionary<string, string[]> Validate(UpdateAssistantSettingsRequest request, out Uri? baseUrl)
    {
        var errors = new Dictionary<string, string[]>();

        if (!ModelEndpointRules.TryParseBaseUrl(request.BaseUrl, out baseUrl))
        {
            errors["baseUrl"] = [ModelEndpointRules.InvalidBaseUrl];
        }

        if (!ModelEndpointRules.IsValidModelName(request.ModelName))
        {
            errors["modelName"] = [ModelEndpointRules.InvalidModelName];
        }

        if (!ModelEndpointRules.IsValidContextTokenBudget(request.ContextTokenBudget))
        {
            errors["contextTokenBudget"] = [ModelEndpointRules.InvalidContextTokenBudget];
        }

        if (!ModelEndpointRules.IsValidSystemPrompt(request.SystemPrompt))
        {
            errors["systemPrompt"] = [ModelEndpointRules.InvalidSystemPrompt];
        }

        return errors;
    }
}

internal sealed record AssistantSettingsResponse(string BaseUrl, string ModelName, bool HasApiKey, int ContextTokenBudget, string SystemPrompt);

internal sealed record UpdateAssistantSettingsRequest(string? BaseUrl, string? ModelName, string? ApiKey, int ContextTokenBudget, string? SystemPrompt);

internal sealed record TestConnectionRequest(string? BaseUrl, string? ApiKey);

internal sealed record ModelConnectionResponse(bool Reachable, IReadOnlyList<string> Models);
