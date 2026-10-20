using System.Security.Cryptography;
using System.Text.Json;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Tools.Mcp;

namespace Quorum.Modules.Agents.Tools.Rag;

/// <summary>
/// The fingerprint of a rag tool's stored configuration. It is the tool's <see cref="Tool.DefinitionHash"/>, so the check every call
/// makes ("the definition I was offered is still the stored one") refuses a call after an administrator changed the binding meanwhile.
/// </summary>
internal static class RagToolHash
{
    public static string Compute(string name, string description, RagConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("description", description);
            WriteIds(writer, "knowledgeBases", configuration.KnowledgeBaseIds);
            WriteIds(writer, "exposedFields", configuration.ExposedFieldIds);
            writer.WritePropertyName("fixedFilters");
            using (var filters = JsonDocument.Parse(configuration.FixedFilters))
            {
                ToolDefinitionHash.WriteCanonical(writer, filters.RootElement);
            }

            writer.WriteNumber("topK", configuration.TopK);
            if (configuration.ScoreThreshold is { } threshold)
            {
                writer.WriteNumber("scoreThreshold", threshold);
            }
            else
            {
                writer.WriteNull("scoreThreshold");
            }

            writer.WriteNumber("maxResultCharacters", configuration.MaxResultCharacters);
            writer.WriteString("scope", configuration.Scope.ToString());
            WriteIds(writer, "scopeTenants", configuration.ScopeTenantIds);
            WriteIds(writer, "scopeAssistants", configuration.ScopeAssistantIds);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    // Sorted: the order an administrator listed them in is not part of what the tool is.
    private static void WriteIds(Utf8JsonWriter writer, string name, IReadOnlyList<Guid> ids)
    {
        writer.WriteStartArray(name);
        foreach (var id in ids.Order())
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }
}
