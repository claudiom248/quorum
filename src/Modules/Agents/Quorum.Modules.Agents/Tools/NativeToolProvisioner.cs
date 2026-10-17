using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// Makes sure a tenant has a row for every native tool and its tool settings. Idempotent: it runs at startup for every
/// tenant (a release can add or redefine native tools) and when a tenant is created.
/// </summary>
internal static class NativeToolProvisioner
{
    /// <returns>The tenant's native tools, after the missing ones were created.</returns>
    public static async Task<IReadOnlyList<Tool>> EnsureAsync(AgentsDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        try
        {
            return await EnsureCoreAsync(db, tenantId, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            // Another instance provisioned the tenant at the same time; what it wrote is what we wanted.
            db.ChangeTracker.Clear();
            return await EnsureCoreAsync(db, tenantId, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<Tool>> EnsureCoreAsync(AgentsDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        // Provisioning runs with no signed-in tenant (startup, setup), so the tenant filter is replaced by an explicit predicate.
        var tools = await db.Tools
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.Origin == ToolOrigin.Native)
            .ToListAsync(cancellationToken);

        foreach (var definition in NativeToolCatalog.All)
        {
            var existing = tools.Find(t => t.Name == definition.Name);
            if (existing is null)
            {
                var created = Tool.CreateNative(tenantId, definition.Name, definition.Description, definition.InputSchema);
                db.Tools.Add(created);
                tools.Add(created);
            }
            else
            {
                existing.Redefine(definition.Description, definition.InputSchema);
            }
        }

        if (!await db.ToolSettings.IgnoreQueryFilters().AnyAsync(s => s.TenantId == tenantId, cancellationToken))
        {
            db.ToolSettings.Add(ToolSettings.CreateDefault(tenantId));
        }

        await db.SaveChangesAsync(cancellationToken);
        return tools;
    }
}
