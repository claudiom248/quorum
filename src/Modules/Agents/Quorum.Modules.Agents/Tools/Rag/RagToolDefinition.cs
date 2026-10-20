using System.Text;
using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools.Rag;

/// <summary>What the model is told about a rag tool, generated from the binding and the live catalog (spec 003 D-11).</summary>
internal sealed record GeneratedRagDefinition(string Description, string InputSchema);

internal static class RagToolDefinition
{
    public const string QueryParameter = "query";

    public const string KnowledgeBaseParameter = "knowledge_base";

    /// <param name="description">The administrator's text for the model.</param>
    /// <param name="knowledgeBases">The knowledge bases the tenant can see, by name.</param>
    /// <param name="exposedFields">The fields the model may filter on that at least one of those knowledge bases maps.</param>
    public static GeneratedRagDefinition Build(string description, IReadOnlyList<KnowledgeBaseInfo> knowledgeBases, IReadOnlyList<FilterFieldInfo> exposedFields)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(knowledgeBases);
        ArgumentNullException.ThrowIfNull(exposedFields);

        return new GeneratedRagDefinition(Describe(description, knowledgeBases), Schema(knowledgeBases, exposedFields));
    }

    private static string Describe(string description, IReadOnlyList<KnowledgeBaseInfo> knowledgeBases)
    {
        var text = new StringBuilder(description.Trim()).Append("\n\nKnowledge bases:");
        foreach (var knowledgeBase in knowledgeBases)
        {
            text.Append("\n- ").Append(knowledgeBase.Name).Append(": ").Append(knowledgeBase.Description);
        }

        return text.ToString();
    }

    private static string Schema(IReadOnlyList<KnowledgeBaseInfo> knowledgeBases, IReadOnlyList<FilterFieldInfo> fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");

            writer.WriteStartObject(QueryParameter);
            writer.WriteString("type", "string");
            writer.WriteString("description", "What to look for, as a short question or a few key words.");
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
                writer.WriteString("description", "Search only this knowledge base. Leave it out to search all of them.");
                writer.WriteEndObject();
            }

            foreach (var field in fields)
            {
                WriteFilter(writer, field);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue(QueryParameter);
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFilter(Utf8JsonWriter writer, FilterFieldInfo field)
    {
        var about = string.IsNullOrWhiteSpace(field.Description) ? field.Name : field.Description.Trim();
        switch (field.Type)
        {
            case FilterFieldType.Text:
                WriteTexts(writer, field.Name, $"{about}. Matches any of the values.");
                break;
            case FilterFieldType.TextList:
                WriteTexts(writer, field.Name, $"{about}. Matches chunks that have any of the values.");
                break;
            case FilterFieldType.Boolean:
                WriteProperty(writer, field.Name, "boolean", about);
                break;
            case FilterFieldType.Number:
                WriteProperty(writer, field.Name + "_min", "number", $"{about}: the lowest value.");
                WriteProperty(writer, field.Name + "_max", "number", $"{about}: the highest value.");
                break;
            case FilterFieldType.Date:
                WriteProperty(writer, field.Name + "_from", "string", $"{about}: the earliest date, ISO 8601 (2024-01-31).");
                WriteProperty(writer, field.Name + "_to", "string", $"{about}: the latest date, ISO 8601 (2024-01-31).");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field.Type, "Unknown field type.");
        }
    }

    private static void WriteProperty(Utf8JsonWriter writer, string name, string type, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", type);
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteTexts(Utf8JsonWriter writer, string name, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "array");
        writer.WriteStartObject("items");
        writer.WriteString("type", "string");
        writer.WriteEndObject();
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }
}
