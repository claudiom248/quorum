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
            var about = new List<string>();
            if (lookup.Info.KnowledgeBaseName is { } knowledgeBase)
            {
                about.Add($"from {knowledgeBase}");
            }

            if (lookup.Info.Unit is { } unit)
            {
                about.Add($"counts {unit}");
            }

            if (about.Count > 0)
            {
                text.Append(" (").Append(string.Join(", ", about)).Append(')');
            }

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

            foreach (var field in fields)
            {
                FilterParameters.WriteFilter(writer, field);
            }

            writer.WriteStartObject(SearchParameter);
            writer.WriteString("type", "string");
            writer.WriteString("description", "Only the values that contain this text, in the value, its label or its synonyms; case does not matter.");
            writer.WriteEndObject();

            writer.WriteStartObject(SortParameter);
            writer.WriteString("type", "string");
            writer.WriteStartArray("enum");
            writer.WriteStringValue(SortByCount);
            writer.WriteStringValue(SortByName);
            writer.WriteEndArray();
            writer.WriteString("description", "count: most documents first (the default). name: alphabetical.");
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
