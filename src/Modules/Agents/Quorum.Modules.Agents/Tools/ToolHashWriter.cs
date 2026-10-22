using System.Text.Json;

namespace Quorum.Modules.Agents.Tools;

/// <summary>The pieces of a configuration fingerprint that rag and lookup tools write the same way.</summary>
internal static class ToolHashWriter
{
    /// <summary>Sorted: the order an administrator listed them in is not part of what the tool is.</summary>
    public static void WriteIds(Utf8JsonWriter writer, string name, IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(ids);

        writer.WriteStartArray(name);
        foreach (var id in ids.Order())
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }

    /// <summary>Written only when there are some, so a tool without aliases keeps the fingerprint it had before they existed.</summary>
    public static void WriteAliases(Utf8JsonWriter writer, string name, IReadOnlyDictionary<Guid, string>? aliases)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (aliases is not { Count: > 0 })
        {
            return;
        }

        writer.WriteStartObject(name);
        foreach (var (id, alias) in aliases.OrderBy(a => a.Key))
        {
            writer.WriteString(id.ToString(), alias);
        }

        writer.WriteEndObject();
    }
}
