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

    /// <summary>The operation that reads the stored lists; the live operations come with their own definition (spec 007 D-12).</summary>
    public const string ValuesOperation = "values";

    public const string SortByName = "name";

    public const string SortByCount = "count";

    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    /// <summary>The parameters of the tool besides the filters: none of them can be the name of a filter.</summary>
    public static IReadOnlyList<string> ReservedParameters { get; } =
        [OperationParameter, LookupParameter, KnowledgeBaseParameter, SearchParameter, SortParameter, LimitParameter, CursorParameter];

    /// <param name="description">The administrator's text for the model.</param>
    /// <param name="lookups">The lookups the tenant can see, by the name the model sees.</param>
    public static GeneratedLookupDefinition Build(string description, IReadOnlyList<OfferedLookup> lookups)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(lookups);

        return new GeneratedLookupDefinition(Describe(description, lookups), Schema(lookups));
    }

    private static string Describe(string description, IReadOnlyList<OfferedLookup> lookups)
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

        return text.ToString();
    }

    private static string Schema(IReadOnlyList<OfferedLookup> lookups)
    {
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
            writer.WriteEndArray();
            writer.WriteString("default", ValuesOperation);
            writer.WriteString("description", "values: read a list of values, with the number of documents of each when the list has counts.");
            writer.WriteEndObject();

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
            writer.WriteString("description", $"How many values to return, up to {MaxLimit}.");
            writer.WriteEndObject();

            writer.WriteStartObject(CursorParameter);
            writer.WriteString("type", "string");
            writer.WriteString("description", "The cursor a previous page returned, to read the next one.");
            writer.WriteEndObject();

            writer.WriteEndObject();
            if (lookups.Count > 1)
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
}
