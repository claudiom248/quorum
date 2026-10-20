using System.Security.Cryptography;
using System.Text.Json;

namespace Quorum.Modules.Agents.Tools.Mcp;

/// <summary>
/// The fingerprint of what a model is told about a tool: name, description and input schema (D-14). Annotations are left
/// out on purpose, they only inform the admin. The schema is canonicalized (object keys sorted), so a server that merely
/// reorders keys does not send the tool back to review.
/// </summary>
internal static class ToolDefinitionHash
{
    public static string Compute(string name, string description, string inputSchema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("description", description);
            writer.WritePropertyName("inputSchema");
            using var schema = JsonDocument.Parse(inputSchema);
            WriteCanonical(writer, schema.RootElement);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    internal static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
