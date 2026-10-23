using System.Text;
using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools.Lookup;

/// <summary>One function the model is offered for a lookup tool: what it does, and the arguments it takes.</summary>
/// <param name="Operation"><c>values</c>, <c>documents</c> or <c>count</c>.</param>
internal sealed record GeneratedLookupFunction(string Operation, string Description, string InputSchema);

/// <summary>What the model is told about a lookup tool, generated from the binding and the live catalog (spec 007 D-12, A1): one function for each thing it can do.</summary>
internal sealed record GeneratedLookupDefinition(IReadOnlyList<GeneratedLookupFunction> Functions);

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

    /// <summary>Reads a list of values, or computes one now when the call carries filters (spec 007 D-7, D-12).</summary>
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

    /// <param name="toolName">The name of the tool, which the functions are named after.</param>
    /// <param name="description">The administrator's text for the model.</param>
    /// <param name="lookups">The lookups the tenant can see, by the name the model sees.</param>
    /// <param name="knowledgeBases">The knowledge bases the live operations query that the tenant can see; with none, only <c>values</c> is offered.</param>
    /// <param name="exposedFields">The fields the model may filter on that a knowledge base of the tool, or of one of its lookups, maps.</param>
    public static GeneratedLookupDefinition Build(
        string toolName,
        string description,
        IReadOnlyList<OfferedLookup> lookups,
        IReadOnlyList<KnowledgeBaseInfo> knowledgeBases,
        IReadOnlyList<FilterFieldInfo> exposedFields)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(knowledgeBases);
        ArgumentNullException.ThrowIfNull(exposedFields);

        var live = knowledgeBases.Count > 0;
        var filters = ListsOfFilters(lookups, exposedFields);
        var introduction = description.Trim();

        List<GeneratedLookupFunction> functions =
        [
            new(ValuesOperation, DescribeValues(introduction, lookups, live), ValuesSchema(lookups, knowledgeBases, live ? exposedFields : [], filters)),
        ];

        if (live)
        {
            functions.Add(new(DocumentsOperation, DescribeDocuments(introduction, knowledgeBases, filters, toolName), DocumentsSchema(knowledgeBases, exposedFields, filters)));
            functions.Add(new(CountOperation, DescribeCount(introduction, knowledgeBases, filters, toolName), CountSchema(knowledgeBases, exposedFields, filters)));
        }

        return new GeneratedLookupDefinition(functions);
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

    private static string DescribeValues(string introduction, IReadOnlyList<OfferedLookup> lookups, bool live)
    {
        var text = new StringBuilder(introduction)
            .Append("\n\nReads a list of values: the values that exist, with the number of documents of each when the list has counts.");
        if (live)
        {
            text.Append(" With filters, a list computed from a field is worked out now among the documents that match them.");
        }

        text.Append("\n\nLists:");
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

        return text.ToString();
    }

    private static string DescribeDocuments(string introduction, IReadOnlyList<KnowledgeBaseInfo> knowledgeBases, IReadOnlyDictionary<string, string> filters, string toolName)
    {
        var text = new StringBuilder(introduction)
            .Append("\n\nLists the documents that match the filters (id, title, link and the fields you can filter on), a page at a time.")
            .Append(" The ids can be given to a search to look only inside those documents.");
        AppendFilterHint(text, filters, toolName);
        AppendKnowledgeBases(text, knowledgeBases);
        return text.ToString();
    }

    private static string DescribeCount(string introduction, IReadOnlyList<KnowledgeBaseInfo> knowledgeBases, IReadOnlyDictionary<string, string> filters, string toolName)
    {
        var text = new StringBuilder(introduction).Append("\n\nCounts the documents that match the filters, exactly.");
        AppendFilterHint(text, filters, toolName);
        AppendKnowledgeBases(text, knowledgeBases);
        return text.ToString();
    }

    // The values of a filter are written exactly as the list holds them, and a model that guesses them gets nothing back.
    private static void AppendFilterHint(StringBuilder text, IReadOnlyDictionary<string, string> filters, string toolName)
    {
        if (filters.Count > 0)
        {
            text.Append(" A filter value must be written exactly as the list of that field holds it: read the list with ")
                .Append(ToolNaming.LookupValues(toolName)).Append('.');
        }
    }

    private static void AppendKnowledgeBases(StringBuilder text, IReadOnlyList<KnowledgeBaseInfo> knowledgeBases)
    {
        text.Append("\n\nKnowledge bases:");
        foreach (var knowledgeBase in knowledgeBases)
        {
            text.Append("\n- ").Append(knowledgeBase.Name).Append(": ").Append(knowledgeBase.Description);
        }
    }

    private static string ValuesSchema(
        IReadOnlyList<OfferedLookup> lookups,
        IReadOnlyList<KnowledgeBaseInfo> knowledgeBases,
        IReadOnlyList<FilterFieldInfo> fields,
        IReadOnlyDictionary<string, string> filterLists)
    {
        var attributes = lookups.Where(l => l.Info.SortableAttributes is { Count: > 0 }).ToList();
        return Object(
            writer =>
            {
                writer.WriteStartObject(LookupParameter);
                writer.WriteString("type", "string");
                writer.WriteStartArray("enum");
                foreach (var lookup in lookups)
                {
                    writer.WriteStringValue(lookup.Name);
                }

                writer.WriteEndArray();
                writer.WriteString("description", "The list to read.");
                writer.WriteEndObject();

                WriteFilters(writer, fields, filterLists);

                writer.WriteStartObject(SearchParameter);
                writer.WriteString("type", "string");
                writer.WriteString("description", "Only the values that contain this text, in the value, its label or its synonyms; case does not matter.");
                writer.WriteEndObject();

                writer.WriteStartObject(SortParameter);
                writer.WriteString("type", "string");
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

                WriteLimit(writer, "How many values to return, up to " + MaxLimit + ".");
                WriteCursor(writer);
            },
            required: lookups.Count > 1 ? [LookupParameter] : []);
    }

    private static string DocumentsSchema(IReadOnlyList<KnowledgeBaseInfo> knowledgeBases, IReadOnlyList<FilterFieldInfo> fields, IReadOnlyDictionary<string, string> filterLists) =>
        Object(
            writer =>
            {
                WriteKnowledgeBase(writer, knowledgeBases);
                WriteFilters(writer, fields, filterLists);
                WriteLimit(writer, "How many documents to return, up to " + MaxLimit + ".");
                WriteCursor(writer);
            },
            required: []);

    private static string CountSchema(IReadOnlyList<KnowledgeBaseInfo> knowledgeBases, IReadOnlyList<FilterFieldInfo> fields, IReadOnlyDictionary<string, string> filterLists) =>
        Object(
            writer =>
            {
                WriteKnowledgeBase(writer, knowledgeBases);
                WriteFilters(writer, fields, filterLists);
            },
            required: []);

    private static string Object(Action<Utf8JsonWriter> properties, string[] required)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            properties(writer);
            writer.WriteEndObject();
            if (required.Length > 0)
            {
                writer.WriteStartArray("required");
                foreach (var name in required)
                {
                    writer.WriteStringValue(name);
                }

                writer.WriteEndArray();
            }

            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteKnowledgeBase(Utf8JsonWriter writer, IReadOnlyList<KnowledgeBaseInfo> knowledgeBases)
    {
        if (knowledgeBases.Count <= 1)
        {
            return;
        }

        writer.WriteStartObject(KnowledgeBaseParameter);
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        foreach (var knowledgeBase in knowledgeBases)
        {
            writer.WriteStringValue(knowledgeBase.Name);
        }

        writer.WriteEndArray();
        writer.WriteString("description", "Query only this knowledge base. Leave it out to query all of them.");
        writer.WriteEndObject();
    }

    // A filter on a field that one of the lists holds says so: the exact values are there.
    private static void WriteFilters(Utf8JsonWriter writer, IReadOnlyList<FilterFieldInfo> fields, IReadOnlyDictionary<string, string> filterLists)
    {
        foreach (var field in fields)
        {
            FilterParameters.WriteFilter(
                writer,
                filterLists.TryGetValue(field.Name, out var list)
                    ? field with { Description = $"{(string.IsNullOrWhiteSpace(field.Description) ? field.Name : field.Description.Trim())} (the exact values are in the list '{list}')" }
                    : field);
        }
    }

    private static void WriteLimit(Utf8JsonWriter writer, string description)
    {
        writer.WriteStartObject(LimitParameter);
        writer.WriteString("type", "integer");
        writer.WriteNumber("minimum", 1);
        writer.WriteNumber("maximum", MaxLimit);
        writer.WriteNumber("default", DefaultLimit);
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteCursor(Utf8JsonWriter writer)
    {
        writer.WriteStartObject(CursorParameter);
        writer.WriteString("type", "string");
        writer.WriteString("description", "The cursor a previous page returned, to read the next one.");
        writer.WriteEndObject();
    }

    private static string SortText(List<OfferedLookup> entityLists)
    {
        const string text = "count: most documents first, for the lists that count documents (the default). name: alphabetical.";
        return entityLists.Count == 0
            ? text
            : text + " For a list of entities, one of its attributes: the name alone puts the smallest or earliest first, with a '-' in front the largest or latest first ("
                + string.Join("; ", entityLists.Select(l => $"{l.Name}: {string.Join(", ", l.Info.SortableAttributes!)}")) + ").";
    }
}
