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

    public const string DocumentIdsParameter = "document_ids";

    /// <param name="description">The administrator's text for the model.</param>
    /// <param name="knowledgeBases">The knowledge bases the tenant can see, by name.</param>
    /// <param name="exposedFields">The fields the model may filter on that at least one of those knowledge bases maps.</param>
    /// <param name="offerDocumentIds">The tool allows <c>document_ids</c> and at least one of the knowledge bases maps the document id.</param>
    public static GeneratedRagDefinition Build(
        string description,
        IReadOnlyList<KnowledgeBaseInfo> knowledgeBases,
        IReadOnlyList<FilterFieldInfo> exposedFields,
        bool offerDocumentIds = false)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(knowledgeBases);
        ArgumentNullException.ThrowIfNull(exposedFields);

        return new GeneratedRagDefinition(Describe(description, knowledgeBases), Schema(knowledgeBases, exposedFields, offerDocumentIds));
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

    private static string Schema(IReadOnlyList<KnowledgeBaseInfo> knowledgeBases, IReadOnlyList<FilterFieldInfo> fields, bool offerDocumentIds)
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

            if (offerDocumentIds)
            {
                writer.WriteStartObject(DocumentIdsParameter);
                writer.WriteString("type", "array");
                writer.WriteStartObject("items");
                writer.WriteString("type", "string");
                writer.WriteEndObject();
                writer.WriteNumber("maxItems", KnowledgeBaseSearchRequest.MaxDocumentIds);
                writer.WriteString("description", "Search only inside these documents, by the document ids that a listing of documents returned. It narrows every other filter and never widens it.");
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
