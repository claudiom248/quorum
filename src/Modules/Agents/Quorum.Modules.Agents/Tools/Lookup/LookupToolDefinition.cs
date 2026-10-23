using System.Text;
using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools.Lookup;

/// <summary>What the model is told about a lookup tool, generated from the binding and the live catalog (spec 007 D-12).</summary>
internal sealed record GeneratedLookupDefinition(string Description, string InputSchema);

/// <summary>A lookup as the model is offered it: the name it sees, which is an alias when one was given.</summary>
internal sealed record OfferedLookup(string Name, LookupInfo Info);

internal static class LookupToolDefinition
{
    public const string OperationParameter = "operation";

    public const string LookupParameter = "lookup";

    public const string KnowledgeBaseParameter = "knowledge_base";

    public const string SearchParameter = "search";

    public const string SortParameter = "sort";

    public const string LimitParameter = "limit";

    public const string CursorParameter = "cursor";

    /// <summary>The operation that reads the stored lists, or computes one now when the call carries filters (spec 007 D-7, D-12).</summary>
    public const string ValuesOperation = "values";

    /// <summary>Lists the documents that match the filters; offered only when the tool queries knowledge bases.</summary>
    public const string DocumentsOperation = "documents";

    /// <summary>Counts the documents that match the filters; offered only when the tool queries knowledge bases.</summary>
    public const string CountOperation = "count";

    public const string SortByName = "name";

    public const string SortByCount = "count";

    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    /// <summary>The parameters of the tool besides the filters: none of them can be the name of a filter.</summary>
    public static IReadOnlyList<string> ReservedParameters { get; } =
        [OperationParameter, LookupParameter, KnowledgeBaseParameter, SearchParameter, SortParameter, LimitParameter, CursorParameter];

    /// <param name="description">The administrator's text for the model.</param>
    /// <param name="lookups">The lookups the tenant can see, by the name the model sees.</param>
    /// <param name="knowledgeBases">The knowledge bases the live operations query that the tenant can see; with none, only <c>values</c> is offered.</param>
    /// <param name="exposedFields">The fields the model may filter on that a knowledge base of the tool, or of one of its lookups, maps.</param>
    public static GeneratedLookupDefinition Build(
        string description,
        IReadOnlyList<OfferedLookup> lookups,
        IReadOnlyList<KnowledgeBaseInfo> knowledgeBases,
        IReadOnlyList<FilterFieldInfo> exposedFields)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(knowledgeBases);
        ArgumentNullException.ThrowIfNull(exposedFields);

        return new GeneratedLookupDefinition(Describe(description, lookups, knowledgeBases), Schema(lookups, knowledgeBases, exposedFields));
    }

    private static string Describe(string description, IReadOnlyList<OfferedLookup> lookups, IReadOnlyList<KnowledgeBaseInfo> knowledgeBases)
    {
        var text = new StringBuilder(description.Trim()).Append("\n\nLists:");
        foreach (var lookup in lookups)
        {
            text.Append("\n- ").Append(lookup.Name);
            // The model must tell what kind of list it holds: the values that occur, the entities there are, or a reference vocabulary.
            var about = new List<string>();
            switch (lookup.Info.Kind)
            {
                case LookupKindInfo.Computed:
                    about.Add($"from {lookup.Info.KnowledgeBaseName}");
                    if (lookup.Info.Unit is { } unit)
                    {
                        about.Add($"counts {unit}");
                    }

                    about.Add("only the values that occur in the documents");
                    break;
                case LookupKindInfo.Entity:
                    about.Add($"the entities of {lookup.Info.KnowledgeBaseName}, one entry each, with their attributes");
                    if (lookup.Info.SortableAttributes is { Count: > 0 } sortable)
                    {
                        about.Add($"can be ordered by {string.Join(", ", sortable)}");
                    }

                    break;
                default:
                    about.Add("typed by hand; it may hold values that occur in no document");
                    break;
            }

            text.Append(" (").Append(string.Join("; ", about)).Append(')');

            if (!string.IsNullOrWhiteSpace(lookup.Info.Description))
            {
                text.Append(": ").Append(lookup.Info.Description.Trim());
            }
        }

        if (knowledgeBases.Count > 0)
        {
            text.Append("\n\nKnowledge bases:");
            foreach (var knowledgeBase in knowledgeBases)
            {
                text.Append("\n- ").Append(knowledgeBase.Name).Append(": ").Append(knowledgeBase.Description);
            }
        }

        return text.ToString();
    }

    private static string Schema(IReadOnlyList<OfferedLookup> lookups, IReadOnlyList<KnowledgeBaseInfo> knowledgeBases, IReadOnlyList<FilterFieldInfo> fields)
    {
        var live = knowledgeBases.Count > 0;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");

            writer.WriteStartObject(OperationParameter);
            writer.WriteString("type", "string");
            writer.WriteStartArray("enum");
            writer.WriteStringValue(ValuesOperation);
            if (live)
            {
                writer.WriteStringValue(DocumentsOperation);
                writer.WriteStringValue(CountOperation);
            }

            writer.WriteEndArray();
            writer.WriteString("default", ValuesOperation);
            writer.WriteString("description", OperationText(live, fields.Count > 0));
            writer.WriteEndObject();

            writer.WriteStartObject(LookupParameter);
            writer.WriteString("type", "string");
            writer.WriteStartArray("enum");
            foreach (var lookup in lookups)
            {
                writer.WriteStringValue(lookup.Name);
            }

            writer.WriteEndArray();
            writer.WriteString("description", live ? "The list to read. Needed for values." : "The list to read.");
            writer.WriteEndObject();

            if (knowledgeBases.Count > 1)
            {
                writer.WriteStartObject(KnowledgeBaseParameter);
                writer.WriteString("type", "string");
                writer.WriteStartArray("enum");
                foreach (var knowledgeBase in knowledgeBases)
                {
                    writer.WriteStringValue(knowledgeBase.Name);
                }

                writer.WriteEndArray();
                writer.WriteString("description", "For documents and count: query only this knowledge base. Leave it out to query all of them.");
                writer.WriteEndObject();
            }

            // A filter on a field that one of the lists holds says so: the exact values are there, and a model that guesses them gets nothing.
            var lists = ListsOfFilters(lookups, fields);
            foreach (var field in fields)
            {
                FilterParameters.WriteFilter(
                    writer,
                    lists.TryGetValue(field.Name, out var list)
                        ? field with { Description = $"{(string.IsNullOrWhiteSpace(field.Description) ? field.Name : field.Description.Trim())} (the exact values are in the list '{list}')" }
                        : field);
            }

            writer.WriteStartObject(SearchParameter);
            writer.WriteString("type", "string");
            writer.WriteString("description", "Only the values that contain this text, in the value, its label or its synonyms; case does not matter.");
            writer.WriteEndObject();

            writer.WriteStartObject(SortParameter);
            writer.WriteString("type", "string");
            var attributes = lookups.Where(l => l.Info.SortableAttributes is { Count: > 0 }).ToList();
            writer.WriteStartArray("enum");
            writer.WriteStringValue(SortByCount);
            writer.WriteStringValue(SortByName);
            foreach (var attribute in attributes.SelectMany(l => l.Info.SortableAttributes!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(attribute);
                writer.WriteStringValue("-" + attribute);
            }

            writer.WriteEndArray();
            writer.WriteString("description", SortText(attributes));
            writer.WriteEndObject();

            writer.WriteStartObject(LimitParameter);
            writer.WriteString("type", "integer");
            writer.WriteNumber("minimum", 1);
            writer.WriteNumber("maximum", MaxLimit);
            writer.WriteNumber("default", DefaultLimit);
            writer.WriteString("description", $"How many values or documents to return, up to {MaxLimit}.");
            writer.WriteEndObject();

            writer.WriteStartObject(CursorParameter);
            writer.WriteString("type", "string");
            writer.WriteString("description", "The cursor a previous page returned, to read the next one.");
            writer.WriteEndObject();

            writer.WriteEndObject();
            if (lookups.Count > 1 && !live)
            {
                writer.WriteStartArray("required");
                writer.WriteStringValue(LookupParameter);
                writer.WriteEndArray();
            }

            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The list that holds the exact values of each filter parameter: the list of a computed lookup is the list of the field it filters.</summary>
    /// <returns>List name by parameter name.</returns>
    public static IReadOnlyDictionary<string, string> ListsOfFilters(IReadOnlyList<OfferedLookup> lookups, IReadOnlyList<FilterFieldInfo> fields)
    {
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(fields);

        var byField = lookups
            .Where(l => l.Info.Kind == LookupKindInfo.Computed && l.Info.FieldId is not null)
            .GroupBy(l => l.Info.FieldId!.Value)
            .ToDictionary(g => g.Key, g => g.First().Name);
        return fields
            .Where(f => byField.ContainsKey(f.Id))
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => byField[g.First().Id], StringComparer.Ordinal);
    }

    private static string SortText(List<OfferedLookup> entityLists)
    {
        const string text = "count: most documents first, for the lists that count documents (the default). name: alphabetical.";
        return entityLists.Count == 0
            ? text
            : text + " For a list of entities, one of its attributes: the name alone puts the smallest or earliest first, with a '-' in front the largest or latest first ("
                + string.Join("; ", entityLists.Select(l => $"{l.Name}: {string.Join(", ", l.Info.SortableAttributes!)}")) + ").";
    }

    private static string OperationText(bool live, bool filters)
    {
        const string values = "values: read a list of values, with the number of documents of each when the list has counts.";
        if (!live)
        {
            return filters
                ? values + " With filters, a list computed from a field is worked out now among the documents that match them."
                : values;
        }

        return values
            + " With filters, a list computed from a field is worked out now among the documents that match them."
            + " documents: list the documents that match the filters (id, title, link and the fields you can filter on), a page at a time."
            + " count: how many documents match the filters, exactly.";
    }
}
