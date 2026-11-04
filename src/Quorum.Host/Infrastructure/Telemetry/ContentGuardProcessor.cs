using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenTelemetry;
using Quorum.Shared.Telemetry;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// When content capture is on, what the frameworks write on the spans (the arguments and results of tool calls, and the messages,
/// which carry them again in the history) goes through the audit redactor and is cut at the standard attribute limit (spec 016 D-2,
/// AC-7). It runs when a span ends, before the batch exporter takes it, so what leaves the process is always the redacted text.
/// The redactor works on the keys of JSON, so JSON carried inside a string value (a tool result is written as a JSON string) is
/// opened and redacted too. The text of a message is cut but not redacted: it is not under a sensitive key.
/// </summary>
internal sealed class ContentGuardProcessor(int maxCharacters) : BaseProcessor<Activity>
{
    private static readonly string[] GuardedKeys =
    [
        "gen_ai.tool.call.arguments",
        "gen_ai.tool.call.result",
        "gen_ai.input.messages",
        "gen_ai.output.messages",
    ];

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override void OnEnd(Activity activity)
    {
        foreach (var key in GuardedKeys)
        {
            if (activity.GetTagItem(key) is not string text)
            {
                continue;
            }

            var value = RedactText(text);
            activity.SetTag(key, value.Length > maxCharacters ? value[..maxCharacters] : value);
        }
    }

    /// <summary>The text with the values of sensitive keys replaced, at any depth, also inside JSON held in string values.</summary>
    internal static string RedactText(string text) => RedactEmbedded(AuditRedactor.Redact(text).Json);

    private static string RedactEmbedded(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                Write(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
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

            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                writer.WriteStringValue(LooksLikeJson(value) ? RedactText(value) : value);
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>Whether the string is a JSON object or array: text that only starts with a bracket stays as it is.</summary>
    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The standard limit, <c>OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT</c>, or the SDK's default when it is not set or not a number.</summary>
    public static int LimitFrom(string? configured) =>
        int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0 ? limit : 4096;
}
