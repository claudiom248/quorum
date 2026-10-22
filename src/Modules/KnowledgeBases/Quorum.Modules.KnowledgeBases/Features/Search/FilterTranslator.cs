using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Storage;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <param name="Conditions">One per filter, empty when a field is missing: a knowledge base is never searched without a filter it cannot apply.</param>
/// <param name="MissingFields">The dictionary fields the knowledge base did not map.</param>
internal sealed record FilterTranslation(IReadOnlyList<FilterCondition> Conditions, IReadOnlyList<string> MissingFields);

/// <summary>Puts the filters of a search on the payload paths one knowledge base mapped for each dictionary field (spec 003 D-12).</summary>
internal static class FilterTranslator
{
    public static FilterTranslation Translate(IReadOnlyList<FieldFilter> filters, KnowledgeBaseMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(mapping);

        var paths = mapping.Fields.GroupBy(f => f.DictionaryFieldId).ToDictionary(g => g.Key, g => g.First().Path);
        var conditions = new List<FilterCondition>(filters.Count);
        var missing = new List<string>();

        foreach (var filter in filters)
        {
            if (!paths.TryGetValue(filter.Field.Id, out var path))
            {
                if (!missing.Contains(filter.Field.Name, StringComparer.Ordinal))
                {
                    missing.Add(filter.Field.Name);
                }

                continue;
            }

            conditions.Add(filter switch
            {
                AnyOf any => new MatchAnyCondition(path, any.Values),
                IsBoolean flag => new MatchBooleanCondition(path, flag.Value),
                NumberRange range => new NumberRangeCondition(path, range.Min, range.Max),
                DateRange range => new DateRangeCondition(path, range.From?.UtcDateTime, range.To?.UtcDateTime),
                _ => throw new ArgumentOutOfRangeException(nameof(filters), filter, "Unknown filter."),
            });
        }

        return missing.Count == 0 ? new FilterTranslation(conditions, []) : new FilterTranslation([], missing);
    }
}
