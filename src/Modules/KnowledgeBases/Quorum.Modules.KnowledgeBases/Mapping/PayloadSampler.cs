using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Mapping;

/// <summary>
/// Finds the mappable properties of a collection from a sample of its points (D-9). Objects are walked (their properties get
/// dot-notation paths); a property's type is inferred from all its values, and mixed types are proposed as text with a warning.
/// Arrays of objects and lists of lists are not mappable and are reported, not silently dropped.
/// </summary>
internal static class PayloadSampler
{
    private const int MaxDepth = 8;

    // A hostile or just very wide payload must not turn the review screen into thousands of rows.
    private const int MaxProperties = 500;

    public static Discovery Sample(IReadOnlyList<JsonObject> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        var observed = new Dictionary<string, Observed>(StringComparer.Ordinal);
        var order = new List<string>();
        var warnings = new List<InspectWarning>();
        var tooMany = false;

        foreach (var payload in payloads)
        {
            Visit(payload, prefix: string.Empty, depth: 0);
        }

        var properties = new List<DiscoveredProperty>();
        foreach (var path in order)
        {
            var kinds = observed[path];
            if (kinds.Unmappable)
            {
                warnings.Add(new InspectWarning(
                    WarningCodes.PropertyNotMapped,
                    $"'{path}' holds objects or nested lists, which cannot be mapped to a metadata field.",
                    path));
                continue;
            }

            if (kinds.Seen.Count == 0)
            {
                warnings.Add(new InspectWarning(
                    WarningCodes.NoValues,
                    $"'{path}' has no value in the sampled points, so its type is unknown. Add it by hand if it matters.",
                    path));
                continue;
            }

            properties.Add(new DiscoveredProperty(path, Infer(path, kinds, warnings)));
        }

        if (tooMany)
        {
            warnings.Add(new InspectWarning(
                WarningCodes.TooManyProperties,
                $"The payload has more than {MaxProperties} properties; only the first {MaxProperties} are shown.",
                Path: null));
        }

        return new Discovery(properties, warnings);

        void Visit(JsonObject node, string prefix, int depth)
        {
            foreach (var (key, value) in node)
            {
                var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                if (key.Contains('.', StringComparison.Ordinal) || key.Length == 0)
                {
                    // Qdrant reads a dot in a key as nesting, so such a property could not be addressed in a filter.
                    if (warnings.All(w => w.Path != path))
                    {
                        warnings.Add(new InspectWarning(
                            WarningCodes.UnsupportedPropertyName,
                            $"'{path}' has a '.' (or no name) in the property name, which filters cannot address, so it is left out.",
                            path));
                    }

                    continue;
                }

                switch (value)
                {
                    case JsonObject child:
                        if (depth + 1 < MaxDepth)
                        {
                            Visit(child, path, depth + 1);
                        }

                        break;

                    case JsonArray array:
                        Record(path).Add(array);
                        break;

                    case null:
                        Record(path).Add(JsonValueKind.Null);
                        break;

                    case JsonValue scalar:
                        Record(path).Add(scalar.GetValueKind(), scalar);
                        break;
                }
            }
        }

        Observed Record(string path)
        {
            if (observed.TryGetValue(path, out var existing))
            {
                return existing;
            }

            if (order.Count >= MaxProperties)
            {
                tooMany = true;
                return new Observed();
            }

            var created = new Observed();
            observed[path] = created;
            order.Add(path);
            return created;
        }
    }

    private static DictionaryFieldType Infer(string path, Observed observed, List<InspectWarning> warnings)
    {
        if (observed.Seen.Count == 1)
        {
            var only = observed.Seen.Single();
            switch (only)
            {
                case Kind.String:
                    return observed.Strings.All(Iso8601.IsDateOrDateTime) ? DictionaryFieldType.Date : DictionaryFieldType.Text;
                case Kind.Number:
                    return DictionaryFieldType.Number;
                case Kind.Boolean:
                    return DictionaryFieldType.Boolean;
                case Kind.TextList:
                    return DictionaryFieldType.TextList;
                case Kind.OtherList:
                    Warn(path, "a list whose values are not all texts", warnings);
                    return DictionaryFieldType.TextList;
            }
        }

        // Lists of texts and empty lists together are still a list of texts; so is a path that only ever held empty lists.
        if (observed.Seen.IsSubsetOf([Kind.TextList, Kind.EmptyList]))
        {
            return DictionaryFieldType.TextList;
        }

        Warn(path, string.Join(", ", observed.Seen.Select(Describe).Order(StringComparer.Ordinal)), warnings);
        return DictionaryFieldType.Text;
    }

    private static void Warn(string path, string what, List<InspectWarning> warnings) =>
        warnings.Add(new InspectWarning(
            WarningCodes.MixedTypes,
            $"'{path}' does not hold one kind of value in the sampled points ({what}). It is proposed as text; check the data before relying on filters over it.",
            path));

    private static string Describe(Kind kind) => kind switch
    {
        Kind.String => "texts",
        Kind.Number => "numbers",
        Kind.Boolean => "booleans",
        Kind.TextList => "lists of texts",
        Kind.EmptyList => "empty lists",
        _ => "lists of other values",
    };

    private enum Kind
    {
        String,
        Number,
        Boolean,
        TextList,
        OtherList,
        EmptyList,
    }

    private sealed class Observed
    {
        public HashSet<Kind> Seen { get; } = [];

        public List<string> Strings { get; } = [];

        public bool Unmappable { get; private set; }

        public void Add(JsonValueKind kind, JsonValue? value = null)
        {
            switch (kind)
            {
                case JsonValueKind.String:
                    Seen.Add(Kind.String);
                    if (value is not null && value.TryGetValue<string>(out var text))
                    {
                        Strings.Add(text);
                    }

                    break;
                case JsonValueKind.Number:
                    Seen.Add(Kind.Number);
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    Seen.Add(Kind.Boolean);
                    break;
            }
        }

        public void Add(JsonArray array)
        {
            if (array.Count == 0)
            {
                Seen.Add(Kind.EmptyList);
                return;
            }

            if (array.Any(item => item is JsonObject or JsonArray))
            {
                Unmappable = true;
                return;
            }

            var scalars = array.Where(item => item is not null).ToList();
            Seen.Add(scalars.Count > 0 && scalars.All(item => item!.GetValueKind() == JsonValueKind.String) ? Kind.TextList : Kind.OtherList);
        }
    }
}
