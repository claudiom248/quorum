using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Tenancy.Domain;
using Quorum.Modules.Tenancy.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Tenancy.Features.ServiceTokens;

internal sealed class ServiceTokenEndpoints : IEndpointGroup
{
    private static readonly Error TokenNotFound = Error.NotFound("service_token.not_found", "The service token does not exist.");

    public void Map(IEndpointRouteBuilder app)
    {
        var tokens = app.MapGroup("/api/tenants/me/service-tokens")
            .WithTags("Service tokens")
            .RequireAuthorization(TenancyModule.AdminPolicy);

        tokens.MapPost("", CreateAsync).WithName("CreateServiceToken");
        tokens.MapGet("", ListAsync).WithName("ListServiceTokens");
        tokens.MapDelete("/{id:guid}", RevokeAsync).WithName("RevokeServiceToken");
    }

    private static async Task<Results<Ok<CreateResponse>, ProblemHttpResult>> CreateAsync(
        TenancyDbContext db,
        ITenantContext tenantContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");
        var (token, plainText) = ServiceToken.Create(tenantId, clock.GetUtcNow());

        db.ServiceTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new CreateResponse(token.Id, plainText, token.CreatedAt));
    }

    private static async Task<Ok<List<TokenInfo>>> ListAsync(
        TenancyDbContext db,
        CancellationToken cancellationToken)
    {
        var tokens = await db.ServiceTokens
            .AsNoTracking()
            .Select(t => new TokenInfo(t.Id, t.CreatedAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(tokens);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeAsync(
        Guid id,
        TenancyDbContext db,
        CancellationToken cancellationToken)
    {
        var deleted = await db.ServiceTokens
            .Where(t => t.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 0 ? TokenNotFound.ToProblem() : TypedResults.NoContent();
    }
}

internal sealed record CreateResponse(Guid Id, string Token, DateTimeOffset CreatedAt);

internal sealed record TokenInfo(Guid Id, DateTimeOffset CreatedAt);
