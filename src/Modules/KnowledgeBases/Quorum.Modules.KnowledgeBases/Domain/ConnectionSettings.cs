using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quorum.Modules.KnowledgeBases.Domain;

/// <summary>
/// The kind-specific settings of a knowledge base (where it lives, which source, how to reach it): one JSON object per row, stored
/// whole in a single <c>jsonb</c> column. The module does not know its members; the adapter of the knowledge base's kind reads and
/// writes its own record from it. Secrets in it are the encrypted ones: a plain secret never reaches the database.
/// </summary>
internal sealed class ConnectionSettings
{
    // Members are stored as the record that holds them declares them (PascalCase), the shape rows have always had.
    private static readonly JsonSerializerOptions Options = new();

    public ConnectionSettings(JsonObject members)
    {
        ArgumentNullException.ThrowIfNull(members);
        Members = members;
    }

    public JsonObject Members { get; }

    public static ConnectionSettings From<T>(T settings)
        where T : notnull =>
        new(JsonSerializer.SerializeToNode(settings, Options)?.AsObject() ?? throw new InvalidOperationException("Settings must serialize to a JSON object."));

    public static ConnectionSettings Parse(string json) =>
        new(JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("The stored connection is not a JSON object."));

    /// <summary>The settings as the record of the kind that wrote them.</summary>
    public T Read<T>()
        where T : class =>
        Members.Deserialize<T>(Options) ?? throw new JsonException("The stored connection is empty.");

    public string ToJson() => Members.ToJsonString();
}
