using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Tenancy.Contracts;
using Quorum.Modules.Tenancy.Domain;
using Quorum.Modules.Tenancy.Persistence;

namespace Quorum.Modules.Tenancy.Features.ServiceTokens;

internal sealed class TenantServiceTokenValidator(TenancyDbContext db) : ITenantServiceTokenValidator
{
    public async Task<Guid?> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        var hash = ServiceToken.HashToken(token);
        var found = await db.ServiceTokens
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        return found;
    }
}
