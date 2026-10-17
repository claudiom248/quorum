using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// Deletes the audit entries older than each tenant's retention (D-13). It runs outside any request, so there is no tenant in
/// scope: tenants are read from the data and each is purged by its own setting, or by the default when it has none.
/// </summary>
internal sealed partial class AuditRetention(AgentsDbContext db, TimeProvider clock, ILogger<AuditRetention> logger)
{
    /// <summary>How many entries one delete takes, so a large backlog never becomes one long statement.</summary>
    internal const int BatchSize = 1000;

    /// <summary>The purge as the background job runs it: a failure is logged and the job tries again at its next turn.</summary>
    /// <returns>How many entries were deleted; zero when the purge failed.</returns>
    public async Task<int> TryPurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await PurgeAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            // The log only grows meanwhile; nothing else depends on the purge.
            LogPurgeFailed(logger, exception);
            return 0;
        }
    }

    /// <returns>How many entries were deleted.</returns>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var retentionByTenant = await db.ToolSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToDictionaryAsync(s => s.TenantId, s => s.AuditRetentionDays, cancellationToken);
        var tenants = await db.ToolAuditEntries.IgnoreQueryFilters().Select(e => e.TenantId).Distinct().ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var tenantId in tenants)
        {
            var days = retentionByTenant.GetValueOrDefault(tenantId, ToolSettings.DefaultAuditRetentionDays);
            var cutoff = now.AddDays(-days);
            while (true)
            {
                var batch = await db.ToolAuditEntries
                    .IgnoreQueryFilters()
                    .Where(e => e.TenantId == tenantId && e.StartedAt < cutoff)
                    .OrderBy(e => e.StartedAt)
                    .Select(e => e.Id)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);
                if (batch.Count == 0)
                {
                    break;
                }

                deleted += await db.ToolAuditEntries.IgnoreQueryFilters().Where(e => batch.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
                if (batch.Count < BatchSize)
                {
                    break;
                }
            }
        }

        if (deleted > 0)
        {
            LogPurged(logger, deleted);
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} audit entries past their retention")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The purge of old audit entries failed; it will run again at the next turn")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}

/// <summary>
/// The purge job: once right after startup, for entries that expired while the application was stopped, then once a day. It runs
/// in the background so a large backlog never delays the start. Hosted services start in registration order and the platform
/// database startup is registered before the modules, so the database is ready when this runs.
/// </summary>
internal sealed class AuditRetentionService(IServiceScopeFactory scopeFactory, TimeProvider clock) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PurgeAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PurgeAsync(stoppingToken);
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditRetention>().TryPurgeAsync(cancellationToken);
    }
}
