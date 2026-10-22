using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// Claims the slices that are due and computes them (spec 007 D-10). It runs outside any request, so there is no tenant: it reads
/// with the tenant filter off, as the audit retention does. Nothing here logs a value, a query or a title.
/// </summary>
internal sealed partial class LookupRefresher(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IOptions<LookupOptions> options,
    LookupCache cache,
    ILogger<LookupRefresher> logger)
{
    /// <summary>At most this many computations run at once in the installation, and at most one per knowledge base.</summary>
    internal const int MaxConcurrent = 2;

    /// <summary>A failed slice is tried again after the refresh interval of its knowledge base, or after this, whichever is shorter.</summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    /// <summary>Serialises the claims of every instance: two could otherwise take different slices of one knowledge base.</summary>
    private const long ClaimLockKey = 7_007_001;

    private LookupOptions Options => options.Value;

    /// <summary>Takes the slices that are due and computes them; returns when they are done.</summary>
    /// <returns>How many slices were computed or failed.</returns>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(cancellationToken);
        await Task.WhenAll(claimed.Select(id => ComputeAsync(id, cancellationToken)));
        return claimed.Count;
    }

    /// <summary>
    /// Marks the due slices as being computed by this instance. A slice another instance has locked is skipped, a knowledge base that
    /// already has a computation running is skipped, and a claim that fails leaves the slices due for the next turn.
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> ClaimAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var now = clock.GetUtcNow();
            var expired = now - Options.Lease;

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", cancellationToken);
            var due = await db.LookupSlices
                .FromSql($"""
                    SELECT s.* FROM knowledge_base.lookup_slices s
                    JOIN knowledge_base.lookups l ON l."Id" = s."LookupId"
                    WHERE l."Kind" <> 'Manual'
                      AND s."NextRefreshAt" <= {now}
                      AND (s."ComputingSince" IS NULL OR s."ComputingSince" < {expired})
                    ORDER BY s."NextRefreshAt"
                    FOR UPDATE OF s SKIP LOCKED
                    """)
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
            {
                return [];
            }

            var dueIds = due.Select(s => s.LookupId).ToList();
            var knowledgeBaseOf = await db.Lookups.IgnoreQueryFilters().AsNoTracking()
                .Where(l => dueIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.KnowledgeBaseId, cancellationToken);
            var busy = (await RunningKnowledgeBasesAsync(db, expired, cancellationToken)).ToHashSet();

            var claimed = new List<Guid>();
            foreach (var slice in due)
            {
                if (claimed.Count >= MaxConcurrent)
                {
                    break;
                }

                if (knowledgeBaseOf[slice.LookupId] is not { } knowledgeBaseId || !busy.Add(knowledgeBaseId))
                {
                    continue;
                }

                slice.BeginComputation(now);
                claimed.Add(slice.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return claimed;
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or InvalidOperationException)
        {
            LogClaimFailed(logger, exception.GetType().Name);
            return [];
        }
    }

    /// <summary>The knowledge bases that have a computation running, taking a lease older than the time budget as dead.</summary>
    internal static async Task<List<Guid>> RunningKnowledgeBasesAsync(KnowledgeBasesDbContext db, DateTimeOffset expired, CancellationToken cancellationToken) =>
        await db.Lookups.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.KnowledgeBaseId != null
                        && db.LookupSlices.IgnoreQueryFilters().Any(s => s.LookupId == l.Id && s.ComputingSince >= expired))
            .Select(l => l.KnowledgeBaseId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Computes one claimed slice and swaps its generation in. A failure keeps the last good generation and is stored on the slice; an
    /// interruption (shutdown) gives the slice back still due.
    /// </summary>
    internal async Task ComputeAsync(Guid sliceId, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<LookupStore>();

        var slice = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(s => s.Id == sliceId, stoppingToken);
        if (slice is null)
        {
            return;
        }

        var lookup = await db.Lookups.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == slice.LookupId, stoppingToken);
        var knowledgeBase = lookup.KnowledgeBaseId is { } knowledgeBaseId
            ? await db.KnowledgeBases.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(k => k.Id == knowledgeBaseId, stoppingToken)
            : null;
        if (knowledgeBase is null)
        {
            await store.ReleaseAsync(sliceId, stoppingToken);
            return;
        }

        var retryAfter = TimeSpan.FromHours(knowledgeBase.LookupRefreshHours);
        var finished = false;
        // The budget runs on the application clock, so a test can move it.
        using var timeout = new CancellationTokenSource(Options.ComputationTimeout, clock);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        try
        {
            // What an earlier computation left behind before it died is a generation nobody reads.
            await store.DiscardUnpublishedAsync(sliceId, budget.Token);

            var computer = scope.ServiceProvider.GetRequiredService<ILookupComputer>();
            var computation = await computer.ComputeAsync(new LookupComputationRequest(lookup, slice, knowledgeBase, Options.MaxValuesPerLookup), budget.Token);
            var (ranked, skipped, truncated) = Rank(lookup, computation, Options.MaxValuesPerLookup);

            var generation = slice.CurrentGeneration + 1;
            await store.WriteGenerationAsync(slice, generation, ranked, budget.Token);
            var now = clock.GetUtcNow();
            await store.SwapAsync(sliceId, generation, ranked.Count, skipped, truncated, now, now + retryAfter, budget.Token);
            finished = true;

            await cache.EvictSliceAsync(sliceId, stoppingToken);
            LogComputed(logger, sliceId, ranked.Count, truncated);
        }
        catch (Exception exception) when (IsFailure(exception, stoppingToken))
        {
            var message = exception switch
            {
                LookupComputationException => exception.Message,
                OperationCanceledException => $"The computation did not finish within {Options.ComputationTimeoutMinutes} minutes.",
                _ => "The computation failed unexpectedly.",
            };

            // Only the type is logged: a message from the store or the database could carry a value.
            LogComputationFailed(logger, sliceId, exception.GetType().Name);
            await DiscardAsync(store, sliceId);
            var retryAt = clock.GetUtcNow() + (retryAfter < MaxRetryDelay ? retryAfter : MaxRetryDelay);
            await store.FailAsync(sliceId, message, retryAt, CancellationToken.None);
            finished = true;
        }
        finally
        {
            if (!finished)
            {
                // Shutdown, or a failure that is not one of the above: give the slice back so the next turn takes it.
                await DiscardAsync(store, sliceId);
                await ReleaseAsync(store, sliceId);
            }
        }
    }

    private static bool IsFailure(Exception exception, CancellationToken stoppingToken) =>
        exception switch
        {
            OperationCanceledException => !stoppingToken.IsCancellationRequested,
            LookupComputationException or DbException or DbUpdateException or InvalidOperationException or TimeoutException => true,
            _ => false,
        };

    /// <summary>
    /// A computed lookup is ranked by count, then value; an entity lookup keeps the order the computer gave. Values longer than the
    /// limit are left out and counted, and the list is cut at the ceiling.
    /// </summary>
    private static (List<ComputedValue> Ranked, int Skipped, bool Truncated) Rank(Lookup lookup, LookupComputation computation, int ceiling)
    {
        var kept = computation.Values.Where(v => v.Value.Length is > 0 and <= LookupValue.MaxValueLength).ToList();
        var skipped = computation.Values.Count - kept.Count;
        var ordered = lookup.Kind == LookupKind.Computed
            ? kept.OrderByDescending(v => v.Count ?? 0).ThenBy(v => v.Value, StringComparer.Ordinal).ToList()
            : kept;

        var truncated = computation.Truncated || ordered.Count > ceiling;
        return (ordered.Count > ceiling ? ordered[..ceiling] : ordered, skipped, truncated);
    }

    private async Task DiscardAsync(LookupStore store, Guid sliceId)
    {
        try
        {
            _ = await store.DiscardUnpublishedAsync(sliceId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            // The rows belong to a generation nobody reads; the next computation of the slice removes them first.
            LogComputationFailed(logger, sliceId, exception.GetType().Name);
        }
    }

    private async Task ReleaseAsync(LookupStore store, Guid sliceId)
    {
        try
        {
            await store.ReleaseAsync(sliceId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            // The lease runs out by itself after the time budget.
            LogComputationFailed(logger, sliceId, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Computed lookup slice {SliceId}: {ValueCount} values, truncated {Truncated}")]
    private static partial void LogComputed(ILogger logger, Guid sliceId, int valueCount, bool truncated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The computation of lookup slice {SliceId} failed ({ExceptionType}); the last good list stays")]
    private static partial void LogComputationFailed(ILogger logger, Guid sliceId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The lookup refresh could not claim slices ({ExceptionType}); it will try again at the next turn")]
    private static partial void LogClaimFailed(ILogger logger, string exceptionType);
}
