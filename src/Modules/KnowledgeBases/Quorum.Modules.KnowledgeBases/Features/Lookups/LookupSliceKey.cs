using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Search;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// Identifies a slice of a lookup (spec 007 D-6): the fixed filters of a tool in a canonical form, and the hash of that form. Two tools
/// whose filters mean the same, written in any order, get the same key and so share one list.
/// </summary>
/// <param name="Filters">The canonical filters as JSON, by dictionary field id; <c>[]</c> for the whole knowledge base.</param>
/// <param name="Hash">The SHA-256 of <paramref name="Filters"/> in lower-case hexadecimal.</param>
internal sealed record LookupSliceKey(string Filters, string Hash)
{
    /// <summary>The slice of a tool with no fixed filters.</summary>
    public static LookupSliceKey WholeKnowledgeBase { get; } = From([]);

    /// <summary>
    /// Fields are ordered by id and values by ordinal comparison, repeated values count once, numbers and instants have one spelling.
    /// Text is otherwise kept as it is: values are matched against the payload exactly (spec 007 D-3).
    /// </summary>
    public static LookupSliceKey From(IReadOnlyList<FieldFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var filter in filters.OrderBy(f => f.Field.Id))
            {
                Write(writer, filter);
            }

            writer.WriteEndArray();
        }

        var json = buffer.WrittenSpan;
        return new LookupSliceKey(Encoding.UTF8.GetString(json), Convert.ToHexStringLower(SHA256.HashData(json)));
    }

    private static void Write(Utf8JsonWriter writer, FieldFilter filter)
    {
        writer.WriteStartObject();
        writer.WriteString("field", filter.Field.Id);
        switch (filter)
        {
            case AnyOf any:
                writer.WriteStartArray("anyOf");
                foreach (var value in any.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
                break;

            case IsBoolean flag:
                writer.WriteBoolean("is", flag.Value);
                break;

            case NumberRange range:
                WriteBound(writer, "min", range.Min);
                WriteBound(writer, "max", range.Max);
                break;

            case DateRange range:
                WriteInstant(writer, "from", range.From);
                WriteInstant(writer, "to", range.To);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown filter.");
        }

        writer.WriteEndObject();
    }

    private static void WriteBound(Utf8JsonWriter writer, string name, double? bound)
    {
        if (bound is { } value)
        {
            writer.WriteNumber(name, value);
        }
    }

    private static void WriteInstant(Utf8JsonWriter writer, string name, DateTimeOffset? instant)
    {
        if (instant is { } value)
        {
            writer.WriteString(name, value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
    }
}
