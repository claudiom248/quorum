using System.Security.Cryptography;
using System.Text.Json;
using Quorum.Modules.Agents.Domain;

namespace Quorum.Modules.Agents.Tools.WebFetch;

/// <summary>
/// The fingerprint of a web fetch tool's stored configuration. It is the tool's <see cref="Tool.DefinitionHash"/>, so the check every
/// call makes ("the definition I was offered is still the stored one") refuses a call after an administrator changed the binding meanwhile.
/// </summary>
internal static class WebFetchToolHash
{
    public static string Compute(string name, string description, WebFetchToolConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "web_fetch");
            writer.WriteString("name", name);
            writer.WriteString("description", description);

            // An empty field is itself a choice: a tool that starts inheriting has another fingerprint than one that pinned the number.
            WriteOptional("tokenBudgetPerPage", configuration.TokenBudgetPerPage);
            WriteOptional("totalTokenBudget", configuration.TotalTokenBudget);
            WriteOptional("maxUrlsPerInvocation", configuration.MaxUrlsPerInvocation);
            WriteOptional("timeout", configuration.Timeout);
            writer.WriteString("scope", configuration.Scope.ToString());
            ToolHashWriter.WriteIds(writer, "scopeTenants", configuration.ScopeTenantIds);
            ToolHashWriter.WriteIds(writer, "scopeAssistants", configuration.ScopeAssistantIds);
            writer.WriteEndObject();

            void WriteOptional(string field, int? value)
            {
                if (value is { } number)
                {
                    writer.WriteNumber(field, number);
                }
                else
                {
                    writer.WriteNull(field);
                }
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
