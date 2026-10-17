using System.Text.Encodings.Web;
using System.Text.Json;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// Prepares the arguments of a tool call for the audit log: the values of keys that look sensitive are replaced, and the
/// text is capped. Matching on the key name is a heuristic: a secret under an unexpected key, or inside free text, is not caught.
/// </summary>
internal static class AuditRedactor
{
    public const int MaxArgumentCharacters = 32_000;

    public const string Placeholder = "[redacted]";

    private static readonly string[] SensitiveFragments =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "apikey",
        "api_key",
        "authorization",
        "credential",
        "private_key",
    ];

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <returns>The JSON to store, and whether it was cut at <see cref="MaxArgumentCharacters"/>.</returns>
    public static (string Json, bool Truncated) Redact(string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(argumentsJson);

        string redacted;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                Write(writer, document.RootElement);
            }

            redacted = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            // Text that is not JSON cannot be searched for secrets, so none of it is kept.
            redacted = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["unparsed"] = $"{argumentsJson.Length} characters that are not valid JSON" });
        }

        return redacted.Length > MaxArgumentCharacters ? (redacted[..MaxArgumentCharacters], true) : (redacted, false);
    }

    public static bool IsSensitive(string key) =>
        SensitiveFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSensitive(property.Name))
                    {
                        writer.WriteString(property.Name, Placeholder);
                    }
                    else
                    {
                        writer.WritePropertyName(property.Name);
                        Write(writer, property.Value);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
