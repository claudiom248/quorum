using System.Diagnostics.CodeAnalysis;

namespace Quorum.Modules.Models.Contracts;

/// <summary>Validation of a model's settings (spec 019 D-1, D-2, D-3), shared by the admin API and the setup flow.</summary>
public static class ModelRules
{
    public const int MaxNameLength = 100;

    public const int MaxModelNameLength = 200;

    public const int MaxQueryPrefixLength = 100;

    /// <summary>The bounds of the context budget: the same as the assistant defaults of the platform (spec 013).</summary>
    public const int MinContextTokenBudget = 256;

    public const int MaxContextTokenBudget = 1_000_000;

    public const int MaxEmbeddingDimensions = 65_536;

    public const string InvalidBaseUrl = "Enter the model server's http(s) base URL, e.g. http://ollama:11434/v1.";

    public const string InvalidModelName = "Enter the model name, as the server lists it.";

    public const string NoCapability = "Choose at least one capability: chat, tools or embeddings.";

    public const string ToolsNeedChat = "Tools need the chat capability.";

    public const string ChatNeedsBudget = "A chat model needs a context budget between 256 and 1000000 tokens.";

    public static string InvalidName { get; } = $"Enter a name of at most {MaxNameLength} characters.";

    public static bool TryParseBaseUrl(string? value, [NotNullWhen(true)] out Uri? baseUrl) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out baseUrl)
        && (baseUrl.Scheme == Uri.UriSchemeHttp || baseUrl.Scheme == Uri.UriSchemeHttps);

    public static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxNameLength;

    public static bool IsValidModelName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxModelNameLength;

    public static bool IsValidContextTokenBudget(int budget) => budget is >= MinContextTokenBudget and <= MaxContextTokenBudget;

    /// <summary>At least one capability, and tools only with chat (D-2).</summary>
    public static bool IsValidCapabilities(ModelCapabilities capabilities) =>
        capabilities != ModelCapabilities.None && (!capabilities.HasFlag(ModelCapabilities.Tools) || capabilities.HasFlag(ModelCapabilities.Chat));

    public static bool IsValidQueryPrefix(string? value) => value is null || value.Length <= MaxQueryPrefixLength;

    public static bool IsValidEmbeddingDimensions(int? dimensions) => dimensions is null or > 0 and <= MaxEmbeddingDimensions;

    /// <summary>The name of a model created without one: the model it serves.</summary>
    public static string DefaultName(string modelName)
    {
        var trimmed = modelName.Trim();
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }
}
