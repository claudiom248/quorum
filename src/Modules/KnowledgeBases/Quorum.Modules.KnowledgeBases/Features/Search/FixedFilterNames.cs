using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <summary>
/// The names a fixed filter may use (spec 003 A1-4): first the aliases of the exposed fields, then the names of the other fields the
/// tool's knowledge bases map. A name shared by fields of several clusters is ambiguous and refused, so a filter never lands on a
/// field the administrator did not mean.
/// </summary>
internal sealed class FixedFilterNames
{
    private static readonly string[] Suffixes = ["_min", "_max", "_from", "_to"];

    private readonly Dictionary<string, List<DictionaryField>> _ambiguous;

    private FixedFilterNames(Dictionary<string, FilterField> fields, Dictionary<string, List<DictionaryField>> ambiguous)
    {
        Fields = fields;
        _ambiguous = ambiguous;
    }

    /// <summary>The filter fields by the name a fixed filter uses.</summary>
    public IReadOnlyDictionary<string, FilterField> Fields { get; }

    /// <param name="aliased">The exposed fields by the alias the model sees.</param>
    /// <param name="others">The other fields a fixed filter may name; they are known by their own name.</param>
    public static FixedFilterNames Build(IReadOnlyDictionary<string, DictionaryField> aliased, IEnumerable<DictionaryField> others)
    {
        ArgumentNullException.ThrowIfNull(aliased);
        ArgumentNullException.ThrowIfNull(others);

        var fields = aliased.ToDictionary(pair => pair.Key, pair => new FilterField(pair.Value.Id, pair.Key, pair.Value.Type), StringComparer.Ordinal);
        var ambiguous = new Dictionary<string, List<DictionaryField>>(StringComparer.Ordinal);

        foreach (var group in others.Where(f => !aliased.Values.Any(a => a.Id == f.Id)).GroupBy(f => f.Name, StringComparer.Ordinal))
        {
            var candidates = group.ToList();
            if (fields.ContainsKey(group.Key))
            {
                // An alias is the name the administrator gave on purpose: it wins over a field that happens to be called the same.
                continue;
            }

            if (candidates.Count == 1)
            {
                fields[group.Key] = new FilterField(candidates[0].Id, group.Key, candidates[0].Type);
            }
            else
            {
                ambiguous[group.Key] = candidates;
            }
        }

        return new FixedFilterNames(fields, ambiguous);
    }

    /// <summary>What to tell the administrator when a filter uses an ambiguous name, or <see langword="null"/> when none does.</summary>
    public string? AmbiguityProblem(IEnumerable<string> filterNames)
    {
        ArgumentNullException.ThrowIfNull(filterNames);

        foreach (var name in filterNames.Order(StringComparer.Ordinal))
        {
            var field = BaseName(name);
            if (field is not null && _ambiguous.TryGetValue(field, out var candidates))
            {
                var clusters = string.Join(", ", candidates.Select(c => c.Cluster ?? "no cluster").Order(StringComparer.Ordinal));
                return $"'{field}' matches fields of several clusters ({clusters}). Expose one of them to the model under an alias and use that name for the filter.";
            }
        }

        return null;
    }

    private string? BaseName(string name)
    {
        if (_ambiguous.ContainsKey(name))
        {
            return name;
        }

        foreach (var suffix in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && _ambiguous.ContainsKey(name[..^suffix.Length]))
            {
                return name[..^suffix.Length];
            }
        }

        return null;
    }
}
