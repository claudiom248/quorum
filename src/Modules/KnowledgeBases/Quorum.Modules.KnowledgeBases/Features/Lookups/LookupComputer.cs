using System.Globalization;
using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Storage;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// Computes the values of a slice from the knowledge base's store through the <em>lookups</em> capability of its kind (spec 007
/// D-10, D-15): never a connector of one store directly. The refresher owns time, storage and failure; this only reads, and every
/// message it raises names a field or a cause, never a value.
/// </summary>
internal sealed class LookupComputer(StoreAdapters adapters) : ILookupComputer
{
    /// <summary>The points a scan that counts distinct documents reads at most (spec 007 D-10).</summary>
    internal const int MaxScannedRecords = 5_000_000;

    public async Task<LookupComputation> ComputeAsync(LookupComputationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (lookup, slice, knowledgeBase) = (request.Lookup, request.Slice, request.KnowledgeBase);
        if (lookup.Kind != LookupKind.Computed)
        {
            throw new LookupComputationException("This kind of lookup cannot be computed yet.");
        }

        var adapter = adapters.For(knowledgeBase.Kind);
        if (adapter.Capability<ILookupsCapability>() is not { } capability)
        {
            throw new LookupComputationException("The storage of this knowledge base does not support lookups.");
        }

        var mapping = knowledgeBase.Mapping;
        if (mapping.Fields.FirstOrDefault(f => f.DictionaryFieldId == lookup.DictionaryFieldId && f.Listable) is not { } field)
        {
            throw new LookupComputationException("The mapping no longer lists this field.");
        }

        var conditions = SliceConditions.Read(slice.Filters, mapping);
        if (conditions.Missing)
        {
            throw new LookupComputationException("The knowledge base does not map a field the filters of this list use.");
        }

        var all = conditions.Conditions.ToList();
        string? documentIdPath = null;
        if (mapping.CountedPoint is { } counted)
        {
            all.Add(CountedCondition(counted));
        }
        else
        {
            documentIdPath = mapping.Roles.DocumentId;
        }

        if (!adapter.TryOpen(knowledgeBase.Connection, out var connection))
        {
            throw new LookupComputationException(adapter.SecretUnreadable.Description);
        }

        var required = new[] { field.Path }.Concat(all.Select(c => c.Path)).Distinct(StringComparer.Ordinal).ToList();
        try
        {
            if (await capability.CheckAsync(connection, required, cancellationToken) is { } problem)
            {
                throw new LookupComputationException(problem);
            }

            var query = new DistinctValuesQuery(field.Path, all, documentIdPath, request.MaxValues, MaxScannedRecords);
            var found = await capability.DistinctValuesAsync(connection, query, cancellationToken);
            return new LookupComputation([.. found.Values.Select(v => new ComputedValue(v.Value, v.Count))], found.Truncated);
        }
        catch (StoreException exception)
        {
            // The message of a store exception is safe to show: it never carries a key, a password or an address.
            throw new LookupComputationException(exception.Message, exception);
        }
    }

    /// <summary>The unit a lookup counts under this mapping (spec 007 D-2).</summary>
    public static LookupUnit UnitOf(KnowledgeBaseMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        return mapping.CountedPoint is not null || mapping.Roles.DocumentId is not null ? LookupUnit.Documents : LookupUnit.Chunks;
    }

    private static FilterCondition CountedCondition(CountedPoint counted)
    {
        var value = counted.ToJsonElement();
        return value.ValueKind switch
        {
            JsonValueKind.True => new MatchBooleanCondition(counted.Path, true),
            JsonValueKind.Number => new NumberRangeCondition(counted.Path, value.GetDouble(), value.GetDouble()),
            _ => new MatchAnyCondition(counted.Path, [value.GetString()!]),
        };
    }
}

/// <summary>Turns the canonical filters stored on a slice (<see cref="LookupSliceKey"/>) back into conditions on the payload of one knowledge base.</summary>
internal static class SliceConditions
{
    /// <param name="Missing">A filter names a dictionary field the knowledge base did not map, so the slice does not exist for it.</param>
    internal sealed record Result(IReadOnlyList<FilterCondition> Conditions, bool Missing);

    public static Result Read(string filters, KnowledgeBaseMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var paths = mapping.Fields.GroupBy(f => f.DictionaryFieldId).ToDictionary(g => g.Key, g => g.First().Path);
        var conditions = new List<FilterCondition>();
        using var document = JsonDocument.Parse(filters);
        foreach (var filter in document.RootElement.EnumerateArray())
        {
            if (!paths.TryGetValue(filter.GetProperty("field").GetGuid(), out var path))
            {
                return new Result([], Missing: true);
            }

            conditions.Add(Condition(filter, path));
        }

        return new Result(conditions, Missing: false);
    }

    private static FilterCondition Condition(JsonElement filter, string path)
    {
        if (filter.TryGetProperty("anyOf", out var values))
        {
            return new MatchAnyCondition(path, [.. values.EnumerateArray().Select(v => v.GetString()!)]);
        }

        if (filter.TryGetProperty("is", out var flag))
        {
            return new MatchBooleanCondition(path, flag.GetBoolean());
        }

        if (filter.TryGetProperty("from", out _) || filter.TryGetProperty("to", out _))
        {
            return new DateRangeCondition(path, Instant(filter, "from"), Instant(filter, "to"));
        }

        return new NumberRangeCondition(path, Bound(filter, "min"), Bound(filter, "max"));
    }

    private static double? Bound(JsonElement filter, string name) => filter.TryGetProperty(name, out var bound) ? bound.GetDouble() : null;

    private static DateTime? Instant(JsonElement filter, string name) =>
        filter.TryGetProperty(name, out var instant)
            ? DateTime.Parse(instant.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal)
            : null;
}
