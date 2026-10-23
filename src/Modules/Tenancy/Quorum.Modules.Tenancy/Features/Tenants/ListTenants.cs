using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Tenancy.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Tenancy.Features.Tenants;

internal sealed class TenantEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/tenants", ListAsync)
            .WithTags("Tenants")
            .WithName("ListTenants")
            .RequireAuthorization(TenancyModule.AdminPolicy);

    // Tenants carry no query filter: the scope picker needs every tenant of the installation to share an item with.
    private static async Task<Ok<TenantResponse[]>> ListAsync(TenancyDbContext db, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        var currentId = tenantContext.TenantId;
        var tenants = await db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ThenBy(t => t.Id)
            .Select(t => new TenantResponse(t.Id, t.Name, t.Id == currentId))
            .ToArrayAsync(cancellationToken);

        return TypedResults.Ok(tenants);
    }
}

internal sealed record TenantResponse(Guid Id, string Name, bool Current);
