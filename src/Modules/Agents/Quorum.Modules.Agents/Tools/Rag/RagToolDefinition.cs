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
                FilterParameters.WriteFilter(writer, field);
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
}
