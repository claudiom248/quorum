using System.Security.Cryptography;
using System.Text.Json;
using Quorum.Modules.Agents.Domain;

namespace Quorum.Modules.Agents.Tools.WebSearch;

/// <summary>
/// The fingerprint of a web search tool's stored configuration. It is the tool's <see cref="Tool.DefinitionHash"/>, so the check every
/// call makes ("the definition I was offered is still the stored one") refuses a call after an administrator changed the binding meanwhile.
/// </summary>
internal static class WebSearchToolHash
{
    public static string Compute(string name, string description, WebSearchToolConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "web_search");
            writer.WriteString("name", name);
            writer.WriteString("description", description);
            writer.WriteNumber("topN", configuration.TopN);
            writer.WriteStartArray("searchEngines");
            foreach (var engine in (configuration.SearchEngines ?? []).Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(engine);
            }

            writer.WriteEndArray();
            writer.WriteString("language", configuration.Language);
            writer.WriteNumber("safeSearch", configuration.SafeSearch);
            writer.WriteString("timeRange", configuration.TimeRange);
            writer.WriteNumber("maxInvocationsPerConversation", configuration.MaxInvocationsPerConversation);
            writer.WriteString("scope", configuration.Scope.ToString());
            ToolHashWriter.WriteIds(writer, "scopeTenants", configuration.ScopeTenantIds);
            ToolHashWriter.WriteIds(writer, "scopeAssistants", configuration.ScopeAssistantIds);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
