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
            ToolHashWriter.WriteIds(writer, "knowledgeBases", configuration.KnowledgeBaseIds);
            ToolHashWriter.WriteIds(writer, "exposedFields", configuration.ExposedFieldIds);
            ToolHashWriter.WriteAliases(writer, "exposedAliases", configuration.ExposedAliases);

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
            ToolHashWriter.WriteIds(writer, "scopeTenants", configuration.ScopeTenantIds);
            ToolHashWriter.WriteIds(writer, "scopeAssistants", configuration.ScopeAssistantIds);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
