using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// Lets a request wake the refresh service at once instead of waiting for its next turn, as <em>Refresh now</em> does. The computation
/// still runs on the service, never in the request.
/// </summary>
internal sealed class LookupRefreshSignal : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        try
        {
            _ = _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // The service is already due to wake.
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _wake.WaitAsync(cancellationToken);

    public void Dispose() => _wake.Dispose();
}

/// <summary>
/// Recomputes the lookups that are due (spec 007 D-10): every minute, and when a refresh is asked for. A due slice can start up to a
/// minute late. Nothing runs at start-up, so a restart recomputes nothing before its scheduled time (AC-17); the cache is warmed by
/// <see cref="LookupCacheWarmup"/>.
/// </summary>
internal sealed partial class LookupRefreshService(LookupRefresher refresher, LookupRefreshSignal signal, TimeProvider clock, ILogger<LookupRefreshService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A turn claims one slice per knowledge base, so a knowledge base with several listable fields needs several: they follow one
    /// another at once instead of one a minute. The bound keeps a slice that cannot be settled from holding the turn for ever.
    /// </summary>
    internal const int MaxClaimsPerTurn = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hosted services start one after the other: yield so the rest of the application is not held up by this one.
        await Task.Yield();

        using var timer = new PeriodicTimer(Interval, clock);
        Task<bool>? tick = null;
        Task? wake = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
            wake ??= signal.WaitAsync(stoppingToken);

            var done = await Task.WhenAny(tick, wake);
            if (done == tick)
            {
                if (!await tick)
                {
                    return;
                }

                tick = null;
            }
            else
            {
                await wake;
                wake = null;
            }

            await RunAsync(stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var claim = 0; claim < MaxClaimsPerTurn; claim++)
            {
                if (await refresher.RunDueAsync(cancellationToken) == 0)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A bug in one turn must not end the job for the life of the process; the slices stay due for the next one.
            LogTurnFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A lookup refresh turn failed ({ExceptionType}); it will run again at the next turn")]
    private static partial void LogTurnFailed(ILogger logger, string exceptionType);
}

/// <summary>
/// Fills the cache from the tables once the application is up (spec 007 D-11, AC-17). It reads and computes nothing else, and it
/// does not hold the start: until it is done a read simply fills the entry it needs.
/// </summary>
internal sealed partial class LookupCacheWarmup(IServiceScopeFactory scopes, LookupCache cache, ILogger<LookupCacheWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await WarmAsync(stoppingToken);
    }

    internal async Task WarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var current = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.CurrentGeneration > 0)
                .Join(db.Lookups.IgnoreQueryFilters().AsNoTracking(), s => s.LookupId, l => l.Id, (slice, lookup) => new { slice, lookup })
                .ToListAsync(cancellationToken);

            foreach (var entry in current)
            {
                await cache.WarmAsync(db, entry.lookup, entry.slice, cancellationToken);
            }

            LogWarmed(logger, current.Count);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            // Nothing depends on the warm-up: the first read of a list fills its entry.
            LogWarmupFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Warmed the lookup cache with {SliceCount} lists")]
    private static partial void LogWarmed(ILogger logger, int sliceCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The lookup cache warm-up failed ({ExceptionType}); lists load on their first read")]
    private static partial void LogWarmupFailed(ILogger logger, string exceptionType);
}

/// <summary>Rejects a configuration the lookups cannot run with, at startup.</summary>
internal sealed class LookupOptionsValidator : IValidateOptions<LookupOptions>
{
    public ValidateOptionsResult Validate(string? name, LookupOptions options) =>
        LookupOptions.Validate(options) is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
}
