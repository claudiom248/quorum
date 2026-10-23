using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Storage;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// The attributes of an entity lookup (spec 007 D-4): which dictionary fields are listed beside the name, how each is called in the
/// stored JSON, how a scanned point becomes a value, and how a list is ordered by one of them.
/// </summary>
internal static class EntityAttributes
{
    public const int MaxAttributes = 10;

    /// <summary>A text attribute is cut here: the model reads these values, and they are customer data kept in the platform database (D-9).</summary>
    public const int MaxTextLength = 200;

    /// <summary>The key of each attribute in the stored JSON: the name of its dictionary field, with the cluster in front when two share a name.</summary>
    public static Dictionary<Guid, string> Names(IEnumerable<DictionaryField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var all = fields.ToList();
        var shared = all.GroupBy(f => f.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return all.ToDictionary(f => f.Id, f => f.Cluster is not null && shared.Contains(f.Name) ? $"{f.Cluster}_{f.Name}" : f.Name);
    }

    /// <summary>The attributes a list can be ordered by: the ones of type number or date, since a text has no order a model could rely on.</summary>
    /// <param name="attributeFields">The dictionary fields the knowledge base marks as attributes.</param>
    public static IReadOnlyList<string> Sortable(IEnumerable<DictionaryField> attributeFields)
    {
        ArgumentNullException.ThrowIfNull(attributeFields);

        var all = attributeFields.ToList();
        var keys = Names(all);
        return [.. all.Where(f => f.Type is DictionaryFieldType.Number or DictionaryFieldType.Date).Select(f => keys[f.Id]).Order(StringComparer.Ordinal)];
    }

    /// <summary>The value of an entity: its name and the attributes it has, or <see langword="null"/> for a point with no usable name.</summary>
    public static ComputedValue? ToValue(JsonObject row, string namePath, IReadOnlyList<(string Path, string Name)> attributes)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(attributes);

        if (PayloadPath.Read(row, namePath) is not JsonValue { } node || !node.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var found = new JsonObject();
        foreach (var (path, key) in attributes)
        {
            if (PayloadPath.Read(row, path) is { } attribute && attribute.GetValueKind() != JsonValueKind.Null)
            {
                found[key] = Bounded(attribute);
            }
        }

        return new ComputedValue(name.Trim(), Attributes: found.Count == 0 ? null : found.ToJsonString());
    }

    /// <summary>The key a list is ordered by in one row: a number, or a date as its ticks, or <see langword="null"/> when the row has no such attribute.</summary>
    public static double? SortKey(string? attributes, string name)
    {
        if (attributes is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(attributes);
        if (!document.RootElement.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant) => instant.UtcTicks,
            _ => null,
        };
    }

    private static JsonNode? Bounded(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > MaxTextLength)
        {
            return JsonValue.Create(text[..MaxTextLength]);
        }

        // A node belongs to one parent: the row is discarded, but the copy keeps the attribute independent of it.
        return node.DeepClone();
    }
}
