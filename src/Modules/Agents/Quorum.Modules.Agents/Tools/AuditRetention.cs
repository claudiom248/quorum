using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// Deletes the audit entries older than each tenant's retention (D-13). It runs outside any request, so there is no tenant in
/// scope: tenants are read from the data and each is purged by its own setting, or by the default when it has none.
/// </summary>
internal sealed partial class AuditRetention(AgentsDbContext db, TimeProvider clock, ILogger<AuditRetention> logger)
{
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
            deleted += await db.ToolAuditEntries
                .IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && e.StartedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }

        if (deleted > 0)
        {
            LogPurged(logger, deleted);
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} audit entries past their retention")]
    private static partial void LogPurged(ILogger logger, int count);
}

/// <summary>Purges once at startup, for entries that expired while the application was stopped. A failure must not stop the application from starting.</summary>
internal sealed partial class AuditRetentionInitializer(IServiceScopeFactory scopeFactory, ILogger<AuditRetentionInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<AuditRetention>().PurgeAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            LogPurgeFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The startup purge of old audit entries failed; the daily job will try again")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}

/// <summary>The daily purge. The first tick comes a day after startup; <see cref="AuditRetentionInitializer"/> covers the start.</summary>
internal sealed partial class AuditRetentionService(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<AuditRetentionService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AuditRetention>().PurgeAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is DbException or InvalidOperationException)
            {
                // Next day, then: the log only grows, it does not break anything.
                LogPurgeFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The purge of old audit entries failed; it will run again in a day")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
