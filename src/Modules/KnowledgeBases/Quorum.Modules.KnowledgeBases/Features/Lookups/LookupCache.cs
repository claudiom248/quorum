using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// One generation of one slice, whole and in both orders (spec 007 D-11). It is immutable so the cache hands out the instance itself.
/// </summary>
[ImmutableObject(true)]
internal sealed class CachedLookup(int generation, IReadOnlyList<CachedLookupValue> byPosition, IReadOnlyList<int> nameOrder)
{
    public int Generation { get; } = generation;

    /// <summary>In rank order.</summary>
    public IReadOnlyList<CachedLookupValue> ByPosition { get; } = byPosition;

    /// <summary>Indexes into <see cref="ByPosition"/> in the order of the database for a page by name, so a cursor means the same on both paths.</summary>
    public IReadOnlyList<int> NameOrder { get; } = nameOrder;
}

internal sealed record CachedLookupValue(int Position, LookupPageValue Value);

/// <summary>
/// The lookups in memory, in front of the tables (spec 007 D-11). The tables are the source of truth: an empty cache refills from them
/// on the first read, and a cache that fails is skipped, so a read never fails because of it. Entries are keyed by slice and generation,
/// so a new generation is a new entry and no reader can mix two; the tag of the old one is removed when the generation changes.
/// The service is memory only: no distributed cache is registered, so every instance holds its own.
/// </summary>
internal sealed partial class LookupCache(HybridCache cache, ILogger<LookupCache> logger)
{
    private static readonly HybridCacheEntryOptions EntryOptions = new()
    {
        // The key carries the generation, so an entry never goes stale; the lifetime only bounds the memory of lists nobody reads.
        Expiration = TimeSpan.FromHours(12),
        LocalCacheExpiration = TimeSpan.FromHours(12),
    };

    public async Task<Result<LookupPage>> ReadAsync(KnowledgeBasesDbContext db, Lookup lookup, LookupSlice slice, LookupPageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(request);

        if (slice.CurrentGeneration > 0)
        {
            try
            {
                var entry = await GetAsync(db, lookup, slice, cancellationToken);
                return LookupPager.Page(entry, slice, request);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Only the type is logged: a message from the store or the database could carry a value.
                LogCacheFailed(logger, exception.GetType().Name);
            }
        }

        return await LookupReader.ReadAsync(db, slice, request, cancellationToken);
    }

    /// <summary>Loads the current generation of a slice into the cache without reading it, as the start-up warm-up does.</summary>
    public async Task WarmAsync(KnowledgeBasesDbContext db, Lookup lookup, LookupSlice slice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(slice);

        if (slice.CurrentGeneration > 0)
        {
            _ = await GetAsync(db, lookup, slice, cancellationToken);
        }
    }

    /// <summary>Drops what the cache holds for a slice, as a new generation becomes current. A failure only leaves the old entry to expire.</summary>
    public Task EvictSliceAsync(Guid sliceId, CancellationToken cancellationToken) => EvictAsync(SliceTag(sliceId), cancellationToken);

    /// <summary>Drops everything of a knowledge base, as it is deleted.</summary>
    public Task EvictKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) => EvictAsync(KnowledgeBaseTag(knowledgeBaseId), cancellationToken);

    /// <summary>Drops the slices of one lookup, as a manual lookup is edited or deleted.</summary>
    public Task EvictLookupAsync(Guid lookupId, CancellationToken cancellationToken) => EvictAsync(LookupTag(lookupId), cancellationToken);

    /// <summary>The entry of one generation of one slice.</summary>
    internal static string KeyFor(Guid sliceId, int generation) => $"lookup:{sliceId}:{generation}";

    private static string SliceTag(Guid sliceId) => $"lookup-slice:{sliceId}";

    private static string KnowledgeBaseTag(Guid knowledgeBaseId) => $"lookup-kb:{knowledgeBaseId}";

    private static string LookupTag(Guid lookupId) => $"lookup:{lookupId}";

    private async Task EvictAsync(string tag, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveByTagAsync(tag, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCacheFailed(logger, exception.GetType().Name);
        }
    }

    private ValueTask<CachedLookup> GetAsync(KnowledgeBasesDbContext db, Lookup lookup, LookupSlice slice, CancellationToken cancellationToken)
    {
        var tags = new List<string> { SliceTag(slice.Id), LookupTag(lookup.Id) };
        if (lookup.KnowledgeBaseId is { } knowledgeBaseId)
        {
            tags.Add(KnowledgeBaseTag(knowledgeBaseId));
        }

        var sliceId = slice.Id;
        var generation = slice.CurrentGeneration;
        return cache.GetOrCreateAsync(
            KeyFor(sliceId, generation),
            (db, sliceId, generation),
            static (state, token) => LoadAsync(state.db, state.sliceId, state.generation, token),
            EntryOptions,
            tags,
            cancellationToken);
    }

    private static async ValueTask<CachedLookup> LoadAsync(KnowledgeBasesDbContext db, Guid sliceId, int generation, CancellationToken cancellationToken)
    {
        var rows = await db.LookupValues.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.SliceId == sliceId && v.Generation == generation)
            .OrderBy(v => v.Position)
            .Select(v => new { v.Position, v.Value, v.Label, v.Synonyms, v.DocumentCount, v.Attributes })
            .ToListAsync(cancellationToken);
        var byPosition = rows
            .Select(v => new CachedLookupValue(v.Position, new LookupPageValue(v.Value, v.Label, v.Synonyms, v.DocumentCount, v.Attributes)))
            .ToArray();

        // The database orders by its own collation, so it orders the names too; positions are unique within a generation.
        var namePositions = await db.LookupValues.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.SliceId == sliceId && v.Generation == generation)
            .OrderBy(v => v.Value).ThenBy(v => v.Position)
            .Select(v => v.Position)
            .ToListAsync(cancellationToken);
        var indexOfPosition = new Dictionary<int, int>(byPosition.Length);
        for (var i = 0; i < byPosition.Length; i++)
        {
            indexOfPosition[byPosition[i].Position] = i;
        }

        return new CachedLookup(generation, byPosition, [.. namePositions.Select(p => indexOfPosition[p])]);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The lookup cache failed ({ExceptionType}); the tables answer instead")]
    private static partial void LogCacheFailed(ILogger logger, string exceptionType);
}

/// <summary>Pages a cached generation exactly as <see cref="LookupReader"/> pages the tables.</summary>
internal static class LookupPager
{
    public static Result<LookupPage> Page(CachedLookup entry, LookupSlice slice, LookupPageRequest request)
    {
        var limit = Math.Clamp(request.Limit, 1, LookupReader.MaxLimit);
        var resolved = LookupCursor.Resolve(request, slice);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        var (after, restarted) = (resolved.Value.After, resolved.Value.Restarted);
        var search = request.Search?.Trim();
        var matches = string.IsNullOrEmpty(search) ? null : search;

        var start = 0;
        if (after is { Position: { } position })
        {
            start = request.Sort == LookupSort.Count
                ? IndexOfFirstAfter(entry.ByPosition, position)
                : IndexOfNameAfter(entry, position);
        }

        // One more than the page tells whether there is a next one without counting.
        var page = new List<CachedLookupValue>(limit + 1);
        for (var i = start; i < entry.ByPosition.Count && page.Count <= limit; i++)
        {
            var row = request.Sort == LookupSort.Count ? entry.ByPosition[i] : entry.ByPosition[entry.NameOrder[i]];
            if (matches is null || Matches(row.Value, matches))
            {
                page.Add(row);
            }
        }

        var shown = page.Take(limit).ToList();
        var next = page.Count > limit ? LookupCursor.Encode(entry.Generation, request.Sort, shown[^1].Position, shown[^1].Value.Value) : null;
        return new LookupPage([.. shown.Select(r => r.Value)], next, restarted);
    }

    private static int IndexOfFirstAfter(IReadOnlyList<CachedLookupValue> byPosition, int position)
    {
        for (var i = 0; i < byPosition.Count; i++)
        {
            if (byPosition[i].Position > position)
            {
                return i;
            }
        }

        return byPosition.Count;
    }

    private static int IndexOfNameAfter(CachedLookup entry, int position)
    {
        for (var i = 0; i < entry.NameOrder.Count; i++)
        {
            if (entry.ByPosition[entry.NameOrder[i]].Position == position)
            {
                return i + 1;
            }
        }

        return entry.NameOrder.Count;
    }

    private static bool Matches(LookupPageValue value, string text) =>
        value.Value.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (value.Label?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
        || value.Synonyms.Any(synonym => synonym.Contains(text, StringComparison.OrdinalIgnoreCase));
}
