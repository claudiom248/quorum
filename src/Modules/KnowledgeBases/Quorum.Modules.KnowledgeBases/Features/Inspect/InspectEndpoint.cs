using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Inspect;

/// <summary>Looks at a collection before the administrator registers it (guided registration, D-9).</summary>
internal sealed class InspectEndpoint : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/knowledge-bases")
            .WithTags("Knowledge bases")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage)
            .MapPost("/inspect", InspectAsync)
            .WithName("InspectKnowledgeBase");
    }

    private static async Task<Results<Ok<InspectResponse>, ProblemHttpResult>> InspectAsync(
        InspectRequest request,
        KnowledgeBaseInspector inspector,
        CancellationToken cancellationToken)
    {
        var result = await inspector.InspectAsync(request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
