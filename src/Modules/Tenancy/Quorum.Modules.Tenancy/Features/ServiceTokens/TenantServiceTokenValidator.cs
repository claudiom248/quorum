using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Tenancy.Contracts;
using Quorum.Modules.Tenancy.Persistence;

namespace Quorum.Modules.Tenancy.Features.ServiceTokens;

internal sealed class TenantServiceTokenValidator(TenancyDbContext db) : ITenantServiceTokenValidator
{
    public async Task<Guid?> ValidateAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var token = await db.ServiceTokens
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        return token?.TenantId;
    }
}
