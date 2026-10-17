using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// At startup, gives every existing tenant the native tools and tool settings a release introduced. Tools are created
/// inactive: an upgrade must not change what an installation's assistants do.
/// </summary>
internal sealed class NativeToolsInitializer(IServiceScopeFactory scopeFactory) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();

        // A tenant exists in this module once its assistant was provisioned, which is when setup completes.
        var tenantIds = await db.Assistants.IgnoreQueryFilters().Select(a => a.TenantId).Distinct().ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds)
        {
            await NativeToolProvisioner.EnsureAsync(db, tenantId, cancellationToken);
        }
    }
}
