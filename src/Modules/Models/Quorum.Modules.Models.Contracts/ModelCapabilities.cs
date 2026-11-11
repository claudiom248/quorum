namespace Quorum.Modules.Models.Contracts;

/// <summary>
/// What a model is declared to do (spec 019 D-2). Declared by the administrator, suggested by the connection test. A model may have
/// several, a chat model with embeddings for instance.
/// </summary>
[Flags]
public enum ModelCapabilities
{
    None = 0,
    Chat = 1,

    /// <summary>Tool calling; requires <see cref="Chat"/> (D-2).</summary>
    Tools = 2,
    Embeddings = 4,
}

public static class ModelCapabilitiesExtensions
{
    /// <summary>The names the API speaks: <c>chat</c>, <c>tools</c>, <c>embeddings</c>.</summary>
    public static IReadOnlyList<string> Names(this ModelCapabilities capabilities)
    {
        var names = new List<string>();
        if (capabilities.HasFlag(ModelCapabilities.Chat))
        {
            names.Add("chat");
        }

        if (capabilities.HasFlag(ModelCapabilities.Tools))
        {
            names.Add("tools");
        }

        if (capabilities.HasFlag(ModelCapabilities.Embeddings))
        {
            names.Add("embeddings");
        }

        return names;
    }

    public static bool TryParse(string? name, out ModelCapabilities capability)
    {
        capability = name?.Trim().ToLowerInvariant() switch
        {
            "chat" => ModelCapabilities.Chat,
            "tools" => ModelCapabilities.Tools,
            "embeddings" => ModelCapabilities.Embeddings,
            _ => ModelCapabilities.None,
        };
        return capability != ModelCapabilities.None;
    }
}
