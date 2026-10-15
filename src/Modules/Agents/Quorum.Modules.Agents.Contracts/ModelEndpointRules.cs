using System.Diagnostics.CodeAnalysis;

namespace Quorum.Modules.Agents.Contracts;

/// <summary>Validation of model endpoint settings, shared by the setup wizard and the settings page.</summary>
public static class ModelEndpointRules
{
    public const int MaxModelNameLength = 200;

    public const string InvalidBaseUrl = "Enter the endpoint's http(s) base URL, e.g. http://ollama:11434/v1.";

    public const string InvalidModelName = "Enter the model name, as listed by the endpoint.";

    public static string InvalidContextTokenBudget { get; } =
        $"The context budget must be between {AssistantDefaults.MinContextTokenBudget} and {AssistantDefaults.MaxContextTokenBudget} tokens.";

    public static string InvalidSystemPrompt { get; } =
        $"The system prompt can be at most {AssistantDefaults.MaxSystemPromptLength} characters.";

    public static bool TryParseBaseUrl(string? value, [NotNullWhen(true)] out Uri? baseUrl) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out baseUrl)
        && (baseUrl.Scheme == Uri.UriSchemeHttp || baseUrl.Scheme == Uri.UriSchemeHttps);

    public static bool IsValidModelName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxModelNameLength;

    public static bool IsValidContextTokenBudget(int budget) =>
        budget is >= AssistantDefaults.MinContextTokenBudget and <= AssistantDefaults.MaxContextTokenBudget;

    public static bool IsValidSystemPrompt(string? value) =>
        value is null || value.Length <= AssistantDefaults.MaxSystemPromptLength;
}
