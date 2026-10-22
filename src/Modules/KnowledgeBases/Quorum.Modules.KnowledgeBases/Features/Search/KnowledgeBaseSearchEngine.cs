using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Dictionary;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <summary>What to search for, already checked: the knowledge bases are chosen by the caller, who knows who may see which.</summary>
internal sealed record SearchInput(
    string Query,
    IReadOnlyDictionary<string, JsonElement> Filters,
    IReadOnlyDictionary<string, JsonElement> FixedFilters,
    int TopK,
    double? ScoreThreshold,
    int? MaxResultCharacters,
    IReadOnlyDictionary<string, Guid>? FilterFields = null,
    IReadOnlyList<Guid>? FixedFilterFieldIds = null);

/// <summary>
/// The search of spec 003 (D-12 to D-14): checks the filters, embeds the query once per embedding setup, queries every knowledge
/// base in parallel under one time limit, interleaves their results and cuts them to the limits. A knowledge base that fails
/// never takes the others down; the scores of different knowledge bases are never compared. Quorum only reads from the store.
/// </summary>
internal sealed partial class KnowledgeBaseSearchEngine(
    KnowledgeBasesDbContext db,
    QdrantConnector qdrant,
    IEmbeddings embeddings,
    KnowledgeBaseSecretProtector protector,
    TimeProvider clock,
    ILogger<KnowledgeBaseSearchEngine> logger)
{
    /// <summary>The most one search may take, the embedding and every store together (D-13).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string TimeoutCode = "knowledge_base.search_timeout";
    private const string ApiKeyCode = "knowledge_base.api_key_unreadable";

    public async Task<Result<KnowledgeBaseSearchResponse>> SearchAsync(IReadOnlyList<KnowledgeBase> bases, SearchInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bases);
        ArgumentNullException.ThrowIfNull(input);

        var started = clock.GetTimestamp();
        var filters = await ParseFiltersAsync(input, bases, cancellationToken);
        if (!filters.IsSuccess)
        {
            return filters.Error;
        }

        // By name, so that the interleaving and the notes come out the same every time.
        var searchable = new List<(KnowledgeBase Base, FilterTranslation Translation)>();
        var skipped = new List<string>();
        foreach (var knowledgeBase in bases.OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            var translation = FilterTranslator.Translate(filters.Value, knowledgeBase.Mapping);
            if (translation.MissingFields.Count > 0)
            {
                skipped.Add(knowledgeBase.Name);
            }
            else
            {
                searchable.Add((knowledgeBase, translation));
            }
        }

        if (searchable.Count == 0)
        {
            return Error.Unprocessable(
                KnowledgeBaseSearchErrors.FilterUnsupported,
                "No knowledge base can apply these filters: they did not map the fields. Search again without the filters that are not needed.");
        }

        using var limit = new CancellationTokenSource(Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);

        // One embedding per model and prefix, however many knowledge bases share them; they all run at once.
        var vectors = searchable
            .Select(s => (s.Base.EmbeddingModel, s.Base.QueryPrefix))
            .Distinct()
            .ToDictionary(key => key, key => embeddings.EmbedAsync((key.QueryPrefix ?? string.Empty) + input.Query, key.EmbeddingModel, linked.Token));

        var outcomes = await Task.WhenAll(searchable.Select(s =>
            SearchOneAsync(s.Base, s.Translation, vectors[(s.Base.EmbeddingModel, s.Base.QueryPrefix)], input, linked.Token, cancellationToken)));

        await UpdateStatusAsync(outcomes, cancellationToken);

        var failures = outcomes.Where(o => o.Failure is not null).Select(o => o.Failure!).ToList();
        foreach (var outcome in outcomes.Where(o => o.Failure is not null))
        {
            LogFailed(logger, outcome.Base.Id, outcome.Failure!.Code);
        }

        // Taken before logging: the level may be off, and the argument must not be computed for nothing.
        var elapsed = clock.GetElapsedTime(started).TotalMilliseconds;
        if (failures.Count == outcomes.Length)
        {
            LogSearched(logger, bases.Count, 0, failures.Count, skipped.Count, elapsed);
            return Error.Unprocessable(
                KnowledgeBaseSearchErrors.Unavailable,
                failures.Count == 1
                    ? failures[0].Message
                    : string.Join(' ', failures.Select(f => $"{f.Name}: {f.Message}")));
        }

        var ranked = Interleave(outcomes.Where(o => o.Failure is null).Select(o => o.Chunks).ToList()).Take(input.TopK).ToList();
        var (chunks, firstTooLarge) = ApplyCap(ranked, input.MaxResultCharacters);

        LogSearched(logger, bases.Count, chunks.Count, failures.Count, skipped.Count, elapsed);
        return new KnowledgeBaseSearchResponse(chunks, skipped, failures, outcomes.Sum(o => o.Unusable), firstTooLarge);
    }

    /// <summary>Round-robin over the lists, one chunk of each in turn: the best of every knowledge base comes before the second best of any.</summary>
    public static IEnumerable<RetrievedChunk> Interleave(IReadOnlyList<IReadOnlyList<RetrievedChunk>> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);

        for (var rank = 0; lists.Any(list => rank < list.Count); rank++)
        {
            foreach (var list in lists.Where(list => rank < list.Count))
            {
                yield return list[rank];
            }
        }
    }

    /// <summary>
    /// Keeps chunks from the front for as long as their text fits, dropping the rest whole (D-13). When even the first is too large
    /// nothing is kept, and the result says so.
    /// </summary>
    public static (IReadOnlyList<RetrievedChunk> Kept, bool FirstExceededCap) ApplyCap(IReadOnlyList<RetrievedChunk> chunks, int? maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (maxCharacters is not { } cap)
        {
            return (chunks, false);
        }

        var kept = new List<RetrievedChunk>();
        var used = 0;
        foreach (var chunk in chunks)
        {
            if (used + chunk.Text.Length > cap)
            {
                break;
            }

            used += chunk.Text.Length;
            kept.Add(chunk);
        }

        return (kept, kept.Count == 0 && chunks.Count > 0);
    }

    private async Task<Result<IReadOnlyList<FieldFilter>>> ParseFiltersAsync(SearchInput input, IReadOnlyList<KnowledgeBase> bases, CancellationToken cancellationToken)
    {
        var visible = await DictionaryVisibility.Fields(db).AsNoTracking().ToListAsync(cancellationToken);

        // The model's filters use the parameters the caller named (a rag tool's aliases); without them, the names of the fields themselves.
        IReadOnlyDictionary<string, FilterField> fields;
        IReadOnlyDictionary<string, FilterField> fixedFields;
        FixedFilterNames? names = null;
        if (input.FilterFields is { } named)
        {
            fields = named
                .Select(pair => (pair.Key, Field: visible.FirstOrDefault(f => f.Id == pair.Value)))
                .Where(entry => entry.Field is not null)
                .ToDictionary(entry => entry.Key, entry => new FilterField(entry.Field!.Id, entry.Key, entry.Field.Type), StringComparer.Ordinal);

            // A fixed filter may also name the other fields it was saved with, by their own name (spec 003 A1-4): the tool's, not the ones of
            // the knowledge bases this search was narrowed to, or narrowing would turn a filter into an error instead of skipping a knowledge base.
            var mapped = input.FixedFilterFieldIds?.ToHashSet() ?? bases.SelectMany(k => k.Mapping.Fields.Select(f => f.DictionaryFieldId)).ToHashSet();
            names = FixedFilterNames.Build(
                named.Where(pair => visible.Any(f => f.Id == pair.Value)).ToDictionary(pair => pair.Key, pair => visible.First(f => f.Id == pair.Value), StringComparer.Ordinal),
                visible.Where(f => mapped.Contains(f.Id)));
            fixedFields = names.Fields;
        }
        else
        {
            fields = visible
                .OrderBy(f => f.Cluster ?? string.Empty, StringComparer.Ordinal)
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new FilterField(g.First().Id, g.Key, g.First().Type), StringComparer.Ordinal);
            fixedFields = fields;
        }

        var fixedFilters = names?.AmbiguityProblem(input.FixedFilters.Keys) is not null
            ? Result<IReadOnlyList<FieldFilter>>.Failure(Error.Validation(KnowledgeBaseSearchErrors.InvalidFixedFilter, "ambiguous"))
            : SearchFilterParser.Parse(input.FixedFilters, fixedFields, KnowledgeBaseSearchErrors.InvalidFixedFilter, ignoreEmpty: false);
        if (!fixedFilters.IsSuccess)
        {
            // The fixed filters are never shown to the model, so what is wrong with them is not said either.
            return Error.Validation(
                KnowledgeBaseSearchErrors.InvalidFixedFilter,
                "A filter that the tool always applies is not valid for the knowledge bases. An administrator has to correct the tool.");
        }

        var modelFilters = SearchFilterParser.Parse(input.Filters, fields, KnowledgeBaseSearchErrors.InvalidFilter, ignoreEmpty: true);
        if (!modelFilters.IsSuccess)
        {
            return modelFilters.Error;
        }

        // Both always apply: a value the model gives for a field the binding fixed can only narrow the fixed one, never replace it.
        return Result<IReadOnlyList<FieldFilter>>.Success([.. fixedFilters.Value, .. modelFilters.Value]);
    }

    private async Task<Outcome> SearchOneAsync(
        KnowledgeBase knowledgeBase,
        FilterTranslation translation,
        Task<Result<IReadOnlyList<float>>> embedding,
        SearchInput input,
        CancellationToken limited,
        CancellationToken caller)
    {
        try
        {
            var vector = await embedding;
            if (!vector.IsSuccess)
            {
                return Outcome.Failed(knowledgeBase, vector.Error.Code, vector.Error.Description);
            }

            var settings = knowledgeBase.Connection;
            if (!protector.TryUnprotect(settings.ProtectedApiKey, out var apiKey))
            {
                return Outcome.Failed(knowledgeBase, ApiKeyCode, "The knowledge base's API key cannot be read. An administrator has to enter it again.");
            }

            var connection = new QdrantConnection(new Uri(settings.Url), settings.Collection, apiKey, settings.VectorName);
            var hits = await qdrant.QueryAsync(
                connection,
                [.. vector.Value],
                translation.Conditions,
                input.TopK,
                input.ScoreThreshold,
                ChunkReader.PathsOf(knowledgeBase.Mapping.Roles),
                limited);

            var chunks = new List<RetrievedChunk>(hits.Count);
            var unusable = 0;
            foreach (var hit in hits)
            {
                if (ChunkReader.Read(knowledgeBase.Name, knowledgeBase.Mapping.Roles, hit.Score, hit.Row) is { } chunk)
                {
                    chunks.Add(chunk);
                }
                else
                {
                    unusable++;
                }
            }

            return new Outcome(knowledgeBase, chunks, unusable, Failure: null);
        }
        catch (StoreException exception)
        {
            return Outcome.Failed(knowledgeBase, CodeOf(exception.Failure), exception.Message);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return Outcome.Failed(knowledgeBase, TimeoutCode, $"The search did not finish within {Timeout.TotalSeconds:0} seconds.");
        }
    }

    private static string CodeOf(StoreFailure failure) => failure switch
    {
        StoreFailure.Unreachable => WarningCodes.QdrantUnreachable,
        StoreFailure.Unauthorized => WarningCodes.QdrantUnauthorized,
        StoreFailure.NotFound => WarningCodes.CollectionNotFound,
        StoreFailure.Rejected => WarningCodes.DimensionMismatch,
        _ => WarningCodes.QdrantUnexpected,
    };

    /// <summary>
    /// A failed search marks the knowledge base <see cref="KnowledgeBaseStatus.Unavailable"/>, a later one that works brings it
    /// back (D-14). Only the owner's own registrations are touched: what another tenant's search found out is about that tenant's
    /// model endpoint as much as about the store, and must not change what the owner sees. A single-row update that never loads
    /// the entity, so a search never races with an administrator editing the knowledge base.
    /// </summary>
    private async Task UpdateStatusAsync(IEnumerable<Outcome> outcomes, CancellationToken cancellationToken)
    {
        if (db.CurrentTenantId is not { } tenantId)
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var outcome in outcomes)
        {
            var id = outcome.Base.Id;
            var own = db.KnowledgeBases.IgnoreQueryFilters().Where(k => k.Id == id && k.TenantId == tenantId);

            if (outcome.Failure is { } failure)
            {
                var error = failure.Message.Length > KnowledgeBase.MaxErrorLength ? failure.Message[..KnowledgeBase.MaxErrorLength] : failure.Message;
                await own.ExecuteUpdateAsync(
                    set => set
                        .SetProperty(k => k.Status, KnowledgeBaseStatus.Unavailable)
                        .SetProperty(k => k.LastError, error)
                        .SetProperty(k => k.LastVerifiedAt, now),
                    cancellationToken);
            }
            else if (outcome.Base.Status == KnowledgeBaseStatus.Unavailable)
            {
                await own.Where(k => k.Status == KnowledgeBaseStatus.Unavailable).ExecuteUpdateAsync(
                    set => set
                        .SetProperty(k => k.Status, KnowledgeBaseStatus.Available)
                        .SetProperty(k => k.LastError, (string?)null)
                        .SetProperty(k => k.LastVerifiedAt, now),
                    cancellationToken);
            }
        }
    }

    // Only ids, counts, durations and codes: never the query, the chunks, the names of documents or what the store said.
    [LoggerMessage(Level = LogLevel.Information, Message = "Search over {KnowledgeBaseCount} knowledge bases returned {ChunkCount} chunks ({FailedCount} failed, {SkippedCount} skipped) in {ElapsedMilliseconds:0} ms")]
    private static partial void LogSearched(ILogger logger, int knowledgeBaseCount, int chunkCount, int failedCount, int skippedCount, double elapsedMilliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search of knowledge base {KnowledgeBaseId} failed with {Code}")]
    private static partial void LogFailed(ILogger logger, Guid knowledgeBaseId, string code);

    private sealed record Outcome(KnowledgeBase Base, IReadOnlyList<RetrievedChunk> Chunks, int Unusable, FailedKnowledgeBase? Failure)
    {
        public static Outcome Failed(KnowledgeBase knowledgeBase, string code, string message) =>
            new(knowledgeBase, [], 0, new FailedKnowledgeBase(knowledgeBase.Name, code, message));
    }
}
