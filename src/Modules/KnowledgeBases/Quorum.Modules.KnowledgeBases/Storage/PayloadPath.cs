using System.Text.Json.Nodes;

namespace Quorum.Modules.KnowledgeBases.Storage;

/// <summary>Reads a dotted path (<c>meta.department</c>) out of a payload shaped as JSON.</summary>
internal static class PayloadPath
{
    public static JsonNode? Read(JsonObject payload, string path)
    {
        JsonNode? current = payload;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject node || !node.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }
}
