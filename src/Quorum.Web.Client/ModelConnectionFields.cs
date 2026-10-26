namespace Quorum.Web.Client;

/// <summary>What the model form edits. The key is only sent when typed; removing it is its own choice.</summary>
public sealed class ModelConnectionFields
{
    public string BaseUrl { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public int ContextTokenBudget { get; set; }

    public bool SupportsTools { get; set; } = true;

    public string? ApiKey { get; set; }

    public bool RemoveKey { get; set; }

    /// <summary>
    /// The key as the update request carries it: <see langword="null"/> keeps the stored one, an empty string removes it, anything
    /// else replaces it. Typing a key wins over asking to remove it.
    /// </summary>
    public string? KeyToSend() => !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() : RemoveKey ? string.Empty : null;

    public bool IsDirty(ModelConnectionFields saved) =>
        BaseUrl.Trim() != saved.BaseUrl || ModelName.Trim() != saved.ModelName || ContextTokenBudget != saved.ContextTokenBudget
        || SupportsTools != saved.SupportsTools || !string.IsNullOrWhiteSpace(ApiKey) || RemoveKey;

    public ModelConnectionFields Copy() => new()
    {
        BaseUrl = BaseUrl,
        ModelName = ModelName,
        ContextTokenBudget = ContextTokenBudget,
        SupportsTools = SupportsTools,
    };
}
