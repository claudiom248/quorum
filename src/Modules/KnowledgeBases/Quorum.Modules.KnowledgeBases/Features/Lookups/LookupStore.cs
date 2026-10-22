using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// Writes the generations of a slice (spec 007 D-10). A computation writes the values of <c>current + 1</c> in batches, which no
/// reader sees because readers filter on the current generation, and one short transaction then makes them current and deletes the
/// previous generation. It runs outside any request, so every query turns the tenant filter off and names the slice.
/// </summary>
internal sealed class LookupStore(KnowledgeBasesDbContext db)
{
    /// <summary>How many values one insert takes, so a list of tens of thousands never becomes one statement.</summary>
    internal const int BatchSize = 1000;

    public async Task WriteGenerationAsync(LookupSlice slice, int generation, IReadOnlyList<ComputedValue> ranked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(ranked);

        for (var offset = 0; offset < ranked.Count; offset += BatchSize)
        {
            var batch = ranked.Skip(offset).Take(BatchSize).Select((v, i) =>
                LookupValue.Create(slice.TenantId, slice.Id, generation, offset + i, v.Value, v.Count, attributes: v.Attributes));
            db.LookupValues.AddRange(batch);
            await db.SaveChangesAsync(cancellationToken);

            // The rows are written and nothing reads them back here; keeping them tracked would hold the whole list in memory twice.
            foreach (var entry in db.ChangeTracker.Entries<LookupValue>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>Makes <paramref name="generation"/> the one readers see and deletes every other, in one transaction.</summary>
    public async Task SwapAsync(
        Guid sliceId,
        int generation,
        int valueCount,
        int skippedValueCount,
        bool truncated,
        DateTimeOffset now,
        DateTimeOffset nextRefreshAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var slice = await db.LookupSlices.IgnoreQueryFilters().SingleAsync(s => s.Id == sliceId, cancellationToken);
        slice.Publish(generation, valueCount, now, nextRefreshAt, skippedValueCount, truncated);
        await db.SaveChangesAsync(cancellationToken);
        await db.LookupValues.IgnoreQueryFilters().Where(v => v.SliceId == sliceId && v.Generation != generation).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes the rows of a generation nobody reads: what a computation wrote before it failed, or before the process died. The current
    /// generation is read in the same statement, so a swap that lands meanwhile is never undone.
    /// </summary>
    public Task<int> DiscardUnpublishedAsync(Guid sliceId, CancellationToken cancellationToken) =>
        db.LookupValues.IgnoreQueryFilters()
            .Where(v => v.SliceId == sliceId
                        && v.Generation != db.LookupSlices.IgnoreQueryFilters().Where(s => s.Id == sliceId).Select(s => s.CurrentGeneration).First())
            .ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// Keeps the last good generation and says why it is old. With no earlier generation the slice is <c>Failed</c>; the next trigger
    /// is <paramref name="retryAt"/>.
    /// </summary>
    public Task FailAsync(Guid sliceId, string error, DateTimeOffset retryAt, CancellationToken cancellationToken)
    {
        var message = error.Length > LookupSlice.MaxErrorLength ? error[..LookupSlice.MaxErrorLength] : error;
        return db.LookupSlices.IgnoreQueryFilters().Where(s => s.Id == sliceId).ExecuteUpdateAsync(
            set => set
                .SetProperty(s => s.Stale, s => s.CurrentGeneration > 0)
                .SetProperty(s => s.Status, s => s.CurrentGeneration > 0 ? s.Status : LookupStatus.Failed)
                .SetProperty(s => s.LastError, message)
                .SetProperty(s => s.NextRefreshAt, retryAt)
                .SetProperty(s => s.ComputingSince, (DateTimeOffset?)null),
            cancellationToken);
    }

    /// <summary>Gives the slice back unchanged, still due: the computation was interrupted, not failed.</summary>
    public Task ReleaseAsync(Guid sliceId, CancellationToken cancellationToken) =>
        db.LookupSlices.IgnoreQueryFilters().Where(s => s.Id == sliceId)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.ComputingSince, (DateTimeOffset?)null), cancellationToken);
}
