using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Secrets;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.ModelEndpoints;

/// <summary>The model endpoints of the tenant, managed by the admin; agents reference them by id (D-33).</summary>
internal sealed class ModelEndpointEndpoints : IEndpointGroup
{
    private static readonly Error NotFound = Error.NotFound("model_endpoint.not_found", "The model endpoint does not exist.");

    private static readonly Error NameTaken = Error.Conflict("model_endpoint.name_taken", "Another model endpoint already has this name.");

    public void Map(IEndpointRouteBuilder app)
    {
        var endpoints = app.MapGroup("/api/model-endpoints")
            .WithTags("Model endpoints")
            .RequireAuthorization(QuorumPolicies.AgentsManage);

        endpoints.MapGet("/", ListAsync).WithName("ListModelEndpoints");
        endpoints.MapGet("/{id:guid}", GetAsync).WithName("GetModelEndpoint");
        endpoints.MapPost("/", CreateAsync).WithName("CreateModelEndpoint");
        endpoints.MapPost("/test", TestAsync).WithName("TestModelEndpoint");
        endpoints.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateModelEndpoint");
        endpoints.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteModelEndpoint");
    }

    private static async Task<Ok<List<ModelEndpointResponse>>> ListAsync(AgentsDbContext db, CancellationToken cancellationToken)
    {
        var endpoints = await db.ModelEndpoints
            .AsNoTracking()
            .OrderBy(e => e.Name)
            .Select(e => new ModelEndpointResponse(e.Id, e.Name, e.BaseUrl, e.ModelName, e.ProtectedApiKey != null, e.ContextTokenBudget, e.SupportsTools))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(endpoints);
    }

    private static async Task<Results<Ok<ModelEndpointResponse>, ProblemHttpResult>> GetAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken) =>
        await db.ModelEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken) is { } endpoint
            ? TypedResults.Ok(ModelEndpointResponse.From(endpoint))
            : NotFound.ToProblem();

    private static async Task<Results<CreatedAtRoute<ModelEndpointResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        ModelEndpointRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        ApiKeyProtector apiKeyProtector,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");
        if (Validate(request, out var baseUrl) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var name = request.Name!.Trim();
        if (await db.ModelEndpoints.AnyAsync(e => e.Name == name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var endpoint = ModelEndpoint.Create(
            tenantId,
            name,
            baseUrl!,
            request.ModelName!.Trim(),
            string.IsNullOrWhiteSpace(request.ApiKey) ? null : apiKeyProtector.Protect(request.ApiKey.Trim()),
            request.ContextTokenBudget,
            request.SupportsTools ?? true,
            clock.GetUtcNow());
        db.ModelEndpoints.Add(endpoint);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        return TypedResults.CreatedAtRoute(
            ModelEndpointResponse.From(endpoint),
            "GetModelEndpoint",
            new RouteValueDictionary { ["id"] = endpoint.Id });
    }

    /// <summary>Without an API key in the request the probe is anonymous; the stored keys are never read back to the client.</summary>
    private static async Task<Results<Ok<ModelConnectionResult>, ValidationProblem, ProblemHttpResult>> TestAsync(
        TestModelEndpointRequest request,
        IModelEndpointProbe probe,
        CancellationToken cancellationToken)
    {
        if (!ModelEndpointRules.TryParseBaseUrl(request.BaseUrl, out var baseUrl))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = [ModelEndpointRules.InvalidBaseUrl] });
        }

        var result = await probe.ProbeAsync(baseUrl, request.ApiKey, request.ModelName, cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(new ModelConnectionResult(Reachable: true, result.Value.Models, result.Value.SupportsTools))
            : result.Error.ToProblem();
    }

    /// <summary><c>apiKey</c>: omitted or <see langword="null"/> keeps the stored key, an empty string removes it.</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id,
        ModelEndpointRequest request,
        AgentsDbContext db,
        ApiKeyProtector apiKeyProtector,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (Validate(request, out var baseUrl) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await db.ModelEndpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken) is not { } endpoint)
        {
            return NotFound.ToProblem();
        }

        var name = request.Name!.Trim();
        if (await db.ModelEndpoints.AnyAsync(e => e.Name == name && e.Id != id, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var now = clock.GetUtcNow();
        endpoint.Rename(name, now);
        endpoint.Configure(baseUrl!, request.ModelName!.Trim(), request.ContextTokenBudget, request.SupportsTools, now);
        if (request.ApiKey is not null)
        {
            endpoint.ReplaceApiKey(string.IsNullOrWhiteSpace(request.ApiKey) ? null : apiKeyProtector.Protect(request.ApiKey.Trim()), now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        return TypedResults.NoContent();
    }

    /// <summary>Refused while something uses the endpoint: the database would cascade the delete to what points at it.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.ModelEndpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken) is not { } endpoint)
        {
            return NotFound.ToProblem();
        }

        var agents = await db.Agents
            .AsNoTracking()
            .Where(a => a.ModelEndpointId == id)
            .OrderBy(a => a.DisplayName)
            .Select(a => new EndpointReference("agent", a.Id, a.DisplayName))
            .ToListAsync(cancellationToken);
        if (agents.Count > 0)
        {
            return TypedResults.Problem(
                detail: "The model endpoint is in use. Point these to another endpoint first.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "model_endpoint.in_use", ["references"] = agents });
        }

        db.ModelEndpoints.Remove(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static Dictionary<string, string[]> Validate(ModelEndpointRequest request, out Uri? baseUrl)
    {
        var errors = new Dictionary<string, string[]>();

        if (!ModelEndpointRules.IsValidName(request.Name))
        {
            errors["name"] = [ModelEndpointRules.InvalidName];
        }

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

        return errors;
    }
}

internal sealed record ModelEndpointRequest(
    string? Name,
    string? BaseUrl,
    string? ModelName,
    string? ApiKey,
    int ContextTokenBudget,
    bool? SupportsTools);

internal sealed record TestModelEndpointRequest(string? BaseUrl, string? ApiKey, string? ModelName);

internal sealed record ModelEndpointResponse(
    Guid Id,
    string Name,
    string BaseUrl,
    string ModelName,
    bool HasApiKey,
    int ContextTokenBudget,
    bool SupportsTools)
{
    public static ModelEndpointResponse From(ModelEndpoint endpoint) => new(
        endpoint.Id,
        endpoint.Name,
        endpoint.BaseUrl,
        endpoint.ModelName,
        endpoint.ProtectedApiKey is not null,
        endpoint.ContextTokenBudget,
        endpoint.SupportsTools);
}

internal sealed record ModelConnectionResult(bool Reachable, IReadOnlyList<string> Models, bool? SupportsTools);

/// <summary>An entity that blocks a delete, listed in the <c>references</c> extension of the 409 response.</summary>
internal sealed record EndpointReference(string Kind, Guid Id, string Name);
