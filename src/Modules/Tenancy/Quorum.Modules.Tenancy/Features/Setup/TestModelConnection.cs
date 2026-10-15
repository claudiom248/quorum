using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Quorum.Modules.Agents.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Tenancy.Features.Setup;

internal static class TestModelConnection
{
    public static async Task<Results<Ok<ModelConnectionResponse>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        TestModelConnectionRequest request,
        IModelEndpointProbe probe,
        CancellationToken cancellationToken)
    {
        if (!ModelEndpointRules.TryParseBaseUrl(request.BaseUrl, out var baseUrl))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = [ModelEndpointRules.InvalidBaseUrl] });
        }

        var result = await probe.ProbeAsync(baseUrl, request.ApiKey, cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(new ModelConnectionResponse(Reachable: true, result.Value.Models))
            : result.Error.ToProblem();
    }
}

internal sealed record TestModelConnectionRequest(string? BaseUrl, string? ApiKey);

internal sealed record ModelConnectionResponse(bool Reachable, IReadOnlyList<string> Models);
