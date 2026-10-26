using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Dictionary;
using Quorum.Modules.KnowledgeBases.Features.Registry;
using Quorum.Modules.KnowledgeBases.Features.Search;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// The live operations of a lookup tool (spec 007 D-7): documents, count and values among the documents that match filters the model
/// chose, asked to the store now and never read from a stored list. Everything is exact, bounded by the 15 s of the search, and always
/// combined with the fixed filters of the tool. It only reads: a store that fails here does not change the status of its knowledge base.
/// </summary>
internal sealed class LiveLookupOperations(KnowledgeBasesDbContext db, StoreAdapters adapters, IOptions<LookupOptions> options, TimeProvider clock)
{
    /// <summary>The most one call takes, the same as a search (spec 003 D-13).</summary>
    public static readonly TimeSpan Timeout = KnowledgeBaseSearchEngine.Timeout;

    /// <summary>The records one page of documents reads when it has to tell documents apart itself (spec 007 "Edge Cases").</summary>
    internal const int MaxRecordsPerCall = 1000;

    /// <summary>
    /// The points a live list scans at most when the knowledge base has no counted point: the full scan of the background computation
    /// (spec 007 D-10) would not fit the timeout and holds its pairs in memory. A list cut here is marked truncated.
    /// </summary>
    internal const int MaxLiveScannedRecords = 50_000;

    internal const int MaxLimit = 200;

    private const int MaxFieldTextLength = 300;

    private const int MaxListedItems = 20;

    private const string NoFilterMessage = "No knowledge base can apply these filters: they do not map the fields. Ask again without the filters that are not needed.";

    private const string TimedOutMessage =
        "The query did not finish within 15 seconds. A filter on a field that has no index in the store is slow: try a narrower filter, or one on another field.";

    public async Task<LiveDocuments> DocumentsAsync(LiveDocumentsRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(request.Scope, request.KnowledgeBaseIds, cancellationToken);
        if (prepared.Failure is { } failure)
        {
            return new LiveDocuments(failure.Outcome, failure.Message);
        }

        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var order = prepared.Bases;
        var from = 0;
        DocumentsCursor? resume = null;
        if (request.Cursor is not null)
        {
            resume = DocumentsCursor.Read(request.Cursor);
            from = resume is null ? -1 : order.FindIndex(k => k.Id == resume.KnowledgeBaseId);
            if (from < 0)
            {
                return new LiveDocuments(LiveOutcome.InvalidCursor, "The cursor does not belong to this search. Ask for the first page again, without a cursor.");
            }
        }

        using var limited = new CancellationTokenSource(Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limited.Token);

        var documents = new List<LiveDocument>();
        var skipped = new List<string>();
        var failed = new List<FailedKnowledgeBase>();
        var attempted = 0;
        string? next = null;

        try
        {
            for (var i = from; i < order.Count && documents.Count == 0; i++)
            {
                var knowledgeBase = order[i];
                var target = TargetOf(knowledgeBase, prepared.Filters);
                if (target.Skipped)
                {
                    skipped.Add(knowledgeBase.Name);
                    continue;
                }

                if (target.CannotOpen)
                {
                    attempted++;
                    failed.Add(target.Problem!);
                    continue;
                }

                attempted++;
                try
                {
                    var page = await ReadDocumentsAsync(target, request.ReturnFieldIds, limit, i == from ? resume : null, linked.Token);
                    documents.AddRange(page.Documents);

                    // A page stays within one knowledge base; where it ended is where the next one starts.
                    if (page.Next is not null)
                    {
                        next = page.Next.Write(knowledgeBase.Id);
                    }
                    else if (documents.Count > 0 && i + 1 < order.Count)
                    {
                        next = new DocumentsCursor(order[i + 1].Id, null, 0).Write();
                    }
                }
                catch (StoreException exception)
                {
                    failed.Add(new FailedKnowledgeBase(knowledgeBase.Name, target.Adapter!.CodeOf(exception.Failure), exception.Message));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LiveDocuments(LiveOutcome.TimedOut, TimedOutMessage);
        }

        if (attempted == 0)
        {
            return new LiveDocuments(LiveOutcome.FilterUnsupported, NoFilterMessage, Skipped: skipped);
        }

        if (failed.Count == attempted && documents.Count == 0)
        {
            return new LiveDocuments(LiveOutcome.Failed, FailureText(failed), Skipped: skipped, Failed: failed);
        }

        return new LiveDocuments(LiveOutcome.Ok, Documents: documents, Cursor: next, Skipped: skipped, Failed: failed);
    }

    public async Task<LiveCount> CountAsync(LiveCountRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(request.Scope, request.KnowledgeBaseIds, cancellationToken);
        if (prepared.Failure is { } failure)
        {
            return new LiveCount(failure.Outcome, failure.Message);
        }

        using var limited = new CancellationTokenSource(Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limited.Token);

        var counts = new List<LiveKnowledgeBaseCount>();
        var skipped = new List<string>();
        var failed = new List<FailedKnowledgeBase>();
        var attempted = 0;

        try
        {
            foreach (var knowledgeBase in prepared.Bases)
            {
                var target = TargetOf(knowledgeBase, prepared.Filters);
                if (target.Skipped)
                {
                    skipped.Add(knowledgeBase.Name);
                    continue;
                }

                if (target.CannotOpen)
                {
                    attempted++;
                    failed.Add(target.Problem!);
                    continue;
                }

                attempted++;
                try
                {
                    var counted = target.Counted();
                    var count = await target.Capability!.CountAsync(target.Connection!, counted.Conditions, linked.Token);
                    counts.Add(new LiveKnowledgeBaseCount(knowledgeBase.Name, count, counted.Unit));
                }
                catch (StoreException exception)
                {
                    failed.Add(new FailedKnowledgeBase(knowledgeBase.Name, target.Adapter!.CodeOf(exception.Failure), exception.Message));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LiveCount(LiveOutcome.TimedOut, TimedOutMessage);
        }

        if (attempted == 0)
        {
            return new LiveCount(LiveOutcome.FilterUnsupported, NoFilterMessage, Skipped: skipped);
        }

        return failed.Count == attempted
            ? new LiveCount(LiveOutcome.Failed, FailureText(failed), Skipped: skipped, Failed: failed)
            : new LiveCount(LiveOutcome.Ok, Counts: counts, Skipped: skipped, Failed: failed);
    }

    /// <param name="lookup">A lookup the caller already found visible to the assistant.</param>
    /// <param name="knowledgeBase">Its knowledge base.</param>
    /// <param name="name">The lookup's name for the model.</param>
    public async Task<LiveValues> ValuesAsync(Lookup lookup, KnowledgeBase knowledgeBase, string name, LiveValuesRequest request, CancellationToken cancellationToken)
    {
        if (lookup.Kind == LookupKind.Entity)
        {
            return await EntityValuesAsync(knowledgeBase, name, request, cancellationToken);
        }

        if (lookup.Kind != LookupKind.Computed)
        {
            return new LiveValues(
                LiveOutcome.NotComputed,
                $"The list '{name}' cannot be filtered: it is typed by hand. Read it without filters.");
        }

        var filters = await SearchFilterInputs.ParseAsync(
            db,
            request.Scope.Filters,
            request.Scope.FixedFilters,
            request.Scope.FilterFields,
            request.Scope.FixedFilterFieldIds,
            [knowledgeBase],
            cancellationToken);
        if (!filters.IsSuccess)
        {
            return new LiveValues(OutcomeOf(filters.Error), filters.Error.Description);
        }

        var target = TargetOf(knowledgeBase, filters.Value);
        if (target.Skipped)
        {
            return new LiveValues(
                LiveOutcome.FilterUnsupported,
                $"The knowledge base {knowledgeBase.Name} does not have a field these filters use, so the list cannot be computed with them.");
        }

        if (target.Problem is { } problem)
        {
            return new LiveValues(LiveOutcome.Failed, $"{problem.Name}: {problem.Message}");
        }

        var mapping = knowledgeBase.Mapping;
        if (mapping.Fields.FirstOrDefault(f => f.DictionaryFieldId == lookup.DictionaryFieldId && f.Listable) is not { } field)
        {
            return new LiveValues(LiveOutcome.NotAvailable, $"The list '{name}' is not available.");
        }

        var counted = target.Counted();
        using var limited = new CancellationTokenSource(Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limited.Token);
        try
        {
            if (await target.Capability!.CheckAsync(target.Connection!, [field.Path], linked.Token) is { } missing)
            {
                return new LiveValues(LiveOutcome.Failed, missing);
            }

            var query = new DistinctValuesQuery(
                field.Path,
                counted.Conditions,
                mapping.CountedPoint is null ? mapping.Roles.DocumentId : null,
                options.Value.MaxValuesPerLookup,
                MaxLiveScannedRecords);
            var found = await target.Capability.DistinctValuesAsync(target.Connection!, query, linked.Token);

            var values = found.Values.Where(v => request.Search is not { Length: > 0 } search || v.Value.Contains(search, StringComparison.OrdinalIgnoreCase));
            var ordered = (request.Sort == LookupSortOrder.Name
                ? values.OrderBy(v => v.Value, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Value, StringComparer.Ordinal)
                : values.OrderByDescending(v => v.Count).ThenBy(v => v.Value, StringComparer.Ordinal)).ToList();
            var page = ordered.Take(Math.Clamp(request.Limit, 1, MaxLimit)).ToList();

            return new LiveValues(
                LiveOutcome.Ok,
                Name: name,
                KnowledgeBase: knowledgeBase.Name,
                Unit: LookupComputer.UnitOf(mapping).ToString().ToLowerInvariant(),
                ValueCount: ordered.Count,
                Truncated: found.Truncated,
                Values: [.. page.Select(v => new LookupReadValue(v.Value, Label: null, [], v.Count, Attributes: null))]);
        }
        catch (StoreException exception)
        {
            return new LiveValues(LiveOutcome.Failed, $"{knowledgeBase.Name}: {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LiveValues(LiveOutcome.TimedOut, TimedOutMessage);
        }
    }

    /// <summary>
    /// The entities that match filters on their own fields, read now from the collection that holds them: their name and the attributes
    /// the mapping marks, like the stored list, but exact and as fresh as the store. A scan with a filter needs no index to work; one on
    /// a large collection is faster with it.
    /// </summary>
    private async Task<LiveValues> EntityValuesAsync(KnowledgeBase knowledgeBase, string name, LiveValuesRequest request, CancellationToken cancellationToken)
    {
        var filters = await SearchFilterInputs.ParseAsync(
            db,
            request.Scope.Filters,
            request.Scope.FixedFilters,
            request.Scope.FilterFields,
            request.Scope.FixedFilterFieldIds,
            [knowledgeBase],
            cancellationToken);
        if (!filters.IsSuccess)
        {
            return new LiveValues(OutcomeOf(filters.Error), filters.Error.Description);
        }

        var target = TargetOf(knowledgeBase, filters.Value);
        if (target.Skipped)
        {
            return new LiveValues(
                LiveOutcome.FilterUnsupported,
                $"The knowledge base {knowledgeBase.Name} does not have a field these filters use, so the list cannot be filtered with them.");
        }

        if (target.Problem is { } problem)
        {
            return new LiveValues(LiveOutcome.Failed, $"{problem.Name}: {problem.Message}");
        }

        var mapping = knowledgeBase.Mapping;
        if (mapping.EntityName is not { } namePath)
        {
            return new LiveValues(LiveOutcome.NotAvailable, $"The list '{name}' is not available.");
        }

        var names = await LookupRefresher.AttributeNamesAsync(db, knowledgeBase, cancellationToken);
        var attributes = mapping.Fields
            .Where(f => f.EntityAttribute && names.ContainsKey(f.DictionaryFieldId))
            .Select(f => (f.Path, Name: names[f.DictionaryFieldId]))
            .ToList();
        string[] paths = [namePath, .. attributes.Select(a => a.Path)];
        var ceiling = options.Value.MaxValuesPerLookup;

        using var limited = new CancellationTokenSource(Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limited.Token);
        try
        {
            var found = new List<ComputedValue>();
            string? cursor = null;
            do
            {
                var page = await target.Capability!.ScanAsync(target.Connection!, new RecordQuery(target.Counted().Conditions, paths, LookupComputer.EntityPageSize, cursor), linked.Token);
                found.AddRange(page.Rows.Select(row => EntityAttributes.ToValue(row, namePath, attributes)).OfType<ComputedValue>());
                cursor = page.Cursor;
            }
            while (cursor is not null && found.Count <= ceiling);

            var truncated = cursor is not null || found.Count > ceiling;
            var matching = found.Take(ceiling).Where(v => request.Search is not { Length: > 0 } search || v.Value.Contains(search, StringComparison.OrdinalIgnoreCase));
            var ordered = OrderEntities(matching, request).ToList();

            return new LiveValues(
                LiveOutcome.Ok,
                Name: name,
                KnowledgeBase: knowledgeBase.Name,
                Unit: null,
                ValueCount: ordered.Count,
                Truncated: truncated,
                Values: [.. ordered.Take(Math.Clamp(request.Limit, 1, MaxLimit)).Select(v => new LookupReadValue(v.Value, Label: null, [], Count: null, v.Attributes))]);
        }
        catch (StoreException exception)
        {
            return new LiveValues(LiveOutcome.Failed, $"{knowledgeBase.Name}: {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LiveValues(LiveOutcome.TimedOut, TimedOutMessage);
        }
    }

    // The order of the store unless asked otherwise; an entity without the attribute goes last.
    private static IEnumerable<ComputedValue> OrderEntities(IEnumerable<ComputedValue> values, LiveValuesRequest request)
    {
        if (request.Sort == LookupSortOrder.Name)
        {
            return values.OrderBy(v => v.Value, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Value, StringComparer.Ordinal);
        }

        if (request is { Sort: LookupSortOrder.Attribute, Attribute: { } attribute })
        {
            var keyed = values.Select(v => (Value: v, Key: EntityAttributes.SortKey(v.Attributes, attribute.Name)));
            var present = keyed.Where(k => k.Key is not null);
            var absent = keyed.Where(k => k.Key is null).Select(k => k.Value);
            var ranked = attribute.Descending ? present.OrderByDescending(k => k.Key) : present.OrderBy(k => k.Key);
            return ranked.ThenBy(k => k.Value.Value, StringComparer.Ordinal).Select(k => k.Value).Concat(absent);
        }

        return values;
    }

    public async Task<IReadOnlyList<UnindexedFilter>> UnindexedAsync(UnindexedFiltersQuery query, CancellationToken cancellationToken)
    {
        var ids = query.KnowledgeBaseIds.Distinct().ToList();
        var fieldIds = query.FieldIds.Distinct().ToList();
        if (ids.Count == 0 || (fieldIds.Count == 0 && !query.IncludeDocumentId))
        {
            return [];
        }

        var bases = await KnowledgeBaseVisibility.KnowledgeBases(db).AsNoTracking().Where(k => ids.Contains(k.Id)).OrderBy(k => k.Name).ToListAsync(cancellationToken);
        var names = await DictionaryVisibility.Fields(db).AsNoTracking().Where(f => fieldIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, cancellationToken);

        var unindexed = new List<UnindexedFilter>();
        foreach (var knowledgeBase in bases)
        {
            var adapter = adapters.For(knowledgeBase.Kind);
            if (!adapter.TryOpen(knowledgeBase.Connection, out var connection))
            {
                continue;
            }

            using var limited = new CancellationTokenSource(Timeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limited.Token);
            SourceProbe probe;
            try
            {
                probe = await adapter.ProbeAsync(connection, sample: false, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (probe.Info is not { } source)
            {
                continue;
            }

            if (query.IncludeDocumentId
                && knowledgeBase.Mapping.Roles.DocumentId is { } documentPath
                && !source.IndexedPaths.ContainsKey(documentPath))
            {
                unindexed.Add(new UnindexedFilter(Guid.Empty, "document id", knowledgeBase.Id, knowledgeBase.Name, documentPath));
            }

            foreach (var fieldId in fieldIds)
            {
                if (names.TryGetValue(fieldId, out var fieldName)
                    && knowledgeBase.Mapping.Fields.FirstOrDefault(f => f.DictionaryFieldId == fieldId) is { } mapped
                    && !source.IndexedPaths.ContainsKey(mapped.Path))
                {
                    unindexed.Add(new UnindexedFilter(fieldId, fieldName, knowledgeBase.Id, knowledgeBase.Name, mapped.Path));
                }
            }
        }

        return unindexed;
    }

    private async Task<Prepared> PrepareAsync(LiveScope scope, IReadOnlyList<Guid> knowledgeBaseIds, CancellationToken cancellationToken)
    {
        var ids = knowledgeBaseIds.Distinct().ToList();
        var bases = ids.Count == 0
            ? []
            : await KnowledgeBaseVisibility.ForAssistant(db, scope.AssistantId).AsNoTracking().Where(k => ids.Contains(k.Id)).ToListAsync(cancellationToken);
        if (bases.Count == 0)
        {
            return Prepared.Fail(LiveOutcome.NotAvailable, "None of the knowledge bases can be queried.");
        }

        var filters = await SearchFilterInputs.ParseAsync(
            db,
            scope.Filters,
            scope.FixedFilters,
            scope.FilterFields,
            scope.FixedFilterFieldIds,
            bases,
            cancellationToken);
        return filters.IsSuccess
            ? new Prepared([.. bases.OrderBy(k => k.Name, StringComparer.Ordinal)], filters.Value, null)
            : Prepared.Fail(OutcomeOf(filters.Error), filters.Error.Description);
    }

    private static LiveOutcome OutcomeOf(Error error) =>
        error.Code == KnowledgeBaseSearchErrors.InvalidFixedFilter ? LiveOutcome.InvalidFixedFilter : LiveOutcome.InvalidFilter;

    /// <summary>What one knowledge base needs to answer: the adapter of its kind, an open connection and the filters on its own paths. Skipped when it cannot.</summary>
    private Target TargetOf(KnowledgeBase knowledgeBase, IReadOnlyList<FieldFilter> filters)
    {
        var translation = FilterTranslator.Translate(filters, knowledgeBase.Mapping);
        var adapter = adapters.For(knowledgeBase.Kind);
        if (translation.MissingFields.Count > 0 || adapter.Capability<ILookupsCapability>() is not { } capability)
        {
            return Target.Skip;
        }

        if (!adapter.TryOpen(knowledgeBase.Connection, out var connection))
        {
            return Target.Unusable(new FailedKnowledgeBase(knowledgeBase.Name, adapter.SecretUnreadable.Code, adapter.SecretUnreadable.Description));
        }

        return new Target(knowledgeBase, adapter, capability, connection, translation.Conditions, Problem: null);
    }

    private static async Task<DocumentsPage> ReadDocumentsAsync(
        Target target,
        IReadOnlyList<Guid> returnFieldIds,
        int limit,
        DocumentsCursor? resume,
        CancellationToken cancellationToken)
    {
        var knowledgeBase = target.KnowledgeBase!;
        var (roles, mapping) = (knowledgeBase.Mapping.Roles, knowledgeBase.Mapping);
        var counted = target.Counted();

        // Without a counted point a document is several records: the page reads the records of one call and keeps each document once,
        // and the cursor says how far into that page the documents were taken, so the next page starts at the next record.
        var dedupe = mapping.CountedPoint is null && roles.DocumentId is not null;
        var fields = returnFieldIds
            .Select(id => (Id: id, Path: mapping.Fields.FirstOrDefault(f => f.DictionaryFieldId == id)?.Path))
            .Where(f => f.Path is not null)
            .ToList();
        IReadOnlyList<string> paths =
        [
            .. new[] { roles.DocumentId, roles.Title, roles.SourceUrl }.Concat(fields.Select(f => f.Path)).Where(p => p is not null).Select(p => p!).Distinct(StringComparer.Ordinal),
        ];

        var page = await target.Capability!.ScanAsync(
            target.Connection!,
            new RecordQuery(counted.Conditions, paths, dedupe ? MaxRecordsPerCall : limit, resume?.Inner),
            cancellationToken);

        var documents = new List<LiveDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skip = resume?.Skip ?? 0;
        var position = skip;
        foreach (var row in page.Rows.Skip(skip))
        {
            position++;
            var id = ChunkReader.Reference(row, roles.DocumentId, ChunkReader.MaxReferenceLength);
            if (dedupe && id is not null && !seen.Add(id))
            {
                continue;
            }

            documents.Add(new LiveDocument(
                knowledgeBase.Name,
                id,
                ChunkReader.Reference(row, roles.Title, ChunkReader.MaxReferenceLength),
                ChunkReader.Reference(row, roles.SourceUrl, ChunkReader.MaxUrlLength, truncate: false),
                fields.Select(f => (f.Id, Text: TextOf(row, f.Path!))).Where(f => f.Text is not null).ToDictionary(f => f.Id, f => f.Text!)));
            if (documents.Count == limit)
            {
                break;
            }
        }

        var next = position < page.Rows.Count
            ? new DocumentsCursor(Guid.Empty, resume?.Inner, position)
            : page.Cursor is { } following ? new DocumentsCursor(Guid.Empty, following, 0) : null;
        return new DocumentsPage(documents, next);
    }

    private static string? TextOf(JsonObject row, string path)
    {
        var node = ChunkReader.Find(row, path);
        var text = node switch
        {
            JsonArray array => string.Join(", ", array.Take(MaxListedItems).Select(ScalarText).OfType<string>()),
            _ => ScalarText(node),
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Length <= MaxFieldTextLength ? text : text[..MaxFieldTextLength];
    }

    private static string? ScalarText(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) ? text
            : value.TryGetValue<long>(out var integer) ? integer.ToString(CultureInfo.InvariantCulture)
            : value.TryGetValue<double>(out var number) ? number.ToString("R", CultureInfo.InvariantCulture)
            : value.TryGetValue<bool>(out var flag) ? (flag ? "true" : "false")
            : null;
    }

    private static string FailureText(List<FailedKnowledgeBase> failed) =>
        failed.Count == 1 ? failed[0].Message : string.Join(' ', failed.Select(f => $"{f.Name}: {f.Message}"));

    private sealed record Prepared(List<KnowledgeBase> Bases, IReadOnlyList<FieldFilter> Filters, PreparedFailure? Failure)
    {
        public static Prepared Fail(LiveOutcome outcome, string message) => new([], [], new PreparedFailure(outcome, message));
    }

    private sealed record PreparedFailure(LiveOutcome Outcome, string Message);

    private sealed record DocumentsPage(List<LiveDocument> Documents, DocumentsCursor? Next);
}

/// <summary>One knowledge base ready to be queried, or <see cref="Skip"/> when it cannot apply the filters or has no lookups.</summary>
internal sealed record Target(
    KnowledgeBase? KnowledgeBase,
    IStoreAdapter? Adapter,
    ILookupsCapability? Capability,
    IStoreConnection? Connection,
    IReadOnlyList<FilterCondition> Conditions,
    FailedKnowledgeBase? Problem)
{
    public static Target Skip { get; } = new(null, null, null, null, [], Problem: null);

    /// <summary>The knowledge base cannot be queried at all, for a reason the model may be told.</summary>
    public static Target Unusable(FailedKnowledgeBase problem) => new(null, null, null, null, [], problem);

    /// <summary>The knowledge base has nothing to answer with these filters: it does not map them, or has no lookups.</summary>
    public bool Skipped => KnowledgeBase is null && Problem is null;

    public bool CannotOpen => Problem is not null;

    /// <summary>The conditions with the counted point, which makes one record of each document, and the unit they count (spec 007 D-2).</summary>
    public (IReadOnlyList<FilterCondition> Conditions, string Unit) Counted()
    {
        if (KnowledgeBase!.Mapping.CountedPoint is not { } point)
        {
            return (Conditions, "chunks");
        }

        IReadOnlyList<FilterCondition> conditions = [.. Conditions, LookupComputer.CountedCondition(point)];
        return (conditions, "documents");
    }
}

/// <summary>Where the next page of documents starts: the knowledge base, the store's cursor for the page of records, and how many records of it were already taken.</summary>
internal sealed record DocumentsCursor(Guid KnowledgeBaseId, string? Inner, int Skip)
{
    public static DocumentsCursor? Read(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight(cursor.Length + ((4 - (cursor.Length % 4)) % 4), '='))).Split('|');
            return parts.Length == 3
                && Guid.TryParse(parts[0], out var id)
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var skip)
                ? new DocumentsCursor(id, parts[1].Length == 0 ? null : parts[1], skip)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string Write(Guid? knowledgeBaseId = null)
    {
        var text = $"{knowledgeBaseId ?? KnowledgeBaseId}|{Inner}|{Skip.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
