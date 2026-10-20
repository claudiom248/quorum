using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Registry;

/// <summary>The knowledge bases of the tenant: registered, verified and edited by the admin (D-5, D-8, D-17).</summary>
internal sealed class KnowledgeBaseEndpoints : IEndpointGroup
{
    private static readonly Error NotFound = Error.NotFound("knowledge_base.not_found", "The knowledge base does not exist.");

    public void Map(IEndpointRouteBuilder app)
    {
        var knowledgeBases = app.MapGroup("/api/knowledge-bases")
            .WithTags("Knowledge bases")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage);

        knowledgeBases.MapGet("/", ListAsync).WithName("ListKnowledgeBases");
        knowledgeBases.MapPost("/", CreateAsync).WithName("CreateKnowledgeBase");
        knowledgeBases.MapGet("/{id:guid}", GetAsync).WithName("GetKnowledgeBase");
        knowledgeBases.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateKnowledgeBase");
        knowledgeBases.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteKnowledgeBase");
        knowledgeBases.MapPost("/{id:guid}/verify", VerifyAsync).WithName("VerifyKnowledgeBase");
    }

    private static async Task<Ok<List<KnowledgeBaseListItem>>> ListAsync(KnowledgeBasesDbContext db, CancellationToken cancellationToken)
    {
        var registrations = await KnowledgeBaseVisibility.KnowledgeBases(db).AsNoTracking().OrderBy(k => k.Name).ToListAsync(cancellationToken);
        return TypedResults.Ok(registrations.Select(k => KnowledgeBaseListItem.From(k, db.CurrentTenantId)).ToList());
    }

    private static async Task<Results<Ok<KnowledgeBaseDetail>, ProblemHttpResult>> GetAsync(Guid id, KnowledgeBasesDbContext db, CancellationToken cancellationToken)
    {
        if (await KnowledgeBaseVisibility.KnowledgeBases(db).AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, cancellationToken) is not { } registration)
        {
            return NotFound.ToProblem();
        }

        // Another tenant's knowledge base shows name, description and status: never where it lives or how it is reached.
        if (registration.TenantId != db.CurrentTenantId)
        {
            return TypedResults.Ok(KnowledgeBaseDetail.Shareable(registration));
        }

        var ids = registration.Mapping.Fields.Select(f => f.DictionaryFieldId).ToList();
        var names = await db.DictionaryFields.IgnoreQueryFilters().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, cancellationToken);
        return TypedResults.Ok(KnowledgeBaseDetail.Owned(registration, names));
    }

    private static async Task<Results<Created<RegisteredResponse>, ProblemHttpResult>> CreateAsync(
        KnowledgeBaseRequest request,
        KnowledgeBaseRegistrar registrar,
        CancellationToken cancellationToken)
    {
        var result = await registrar.CreateAsync(request, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/knowledge-bases/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<VerificationResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        KnowledgeBaseRequest request,
        KnowledgeBaseRegistrar registrar,
        CancellationToken cancellationToken)
    {
        var result = await registrar.UpdateAsync(id, request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<VerificationResponse>, ProblemHttpResult>> VerifyAsync(
        Guid id,
        KnowledgeBaseRegistrar registrar,
        CancellationToken cancellationToken)
    {
        var result = await registrar.VerifyAsync(id, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    /// <summary>Removes the registration only: the data in the store is its owner's (D-17).</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, KnowledgeBasesDbContext db, CancellationToken cancellationToken)
    {
        // The tenant filter makes a knowledge base another tenant shares with this one not found here: it is read-only.
        if (await db.KnowledgeBases.SingleOrDefaultAsync(k => k.Id == id, cancellationToken) is not { } registration)
        {
            return NotFound.ToProblem();
        }

        db.KnowledgeBases.Remove(registration);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
