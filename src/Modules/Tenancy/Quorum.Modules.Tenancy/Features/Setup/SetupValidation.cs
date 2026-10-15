using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;
using Quorum.Modules.Agents.Contracts;

namespace Quorum.Modules.Tenancy.Features.Setup;

internal static class SetupValidation
{
    public const string InvalidBaseUrl = "Enter the endpoint's http(s) base URL, e.g. http://ollama:11434/v1.";

    public static bool TryParseBaseUrl(string? value, [NotNullWhen(true)] out Uri? baseUrl) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out baseUrl)
        && (baseUrl.Scheme == Uri.UriSchemeHttp || baseUrl.Scheme == Uri.UriSchemeHttps);

    public static Dictionary<string, string[]> Validate(CompleteSetupRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.Admin is not { } admin)
        {
            errors["admin"] = ["The administrator is required."];
        }
        else
        {
            if (!MailAddress.TryCreate(admin.Email?.Trim(), out var email) || email.Address != admin.Email?.Trim())
            {
                errors["admin.email"] = ["Enter a valid email address."];
            }

            if (string.IsNullOrWhiteSpace(admin.DisplayName) || admin.DisplayName.Trim().Length > 100)
            {
                errors["admin.displayName"] = ["Enter a name of at most 100 characters."];
            }

            if (string.IsNullOrEmpty(admin.Password))
            {
                errors["admin.password"] = ["The password is required."];
            }
        }

        if (request.Model is not { } model)
        {
            errors["model"] = ["The model endpoint is required."];
        }
        else
        {
            if (!TryParseBaseUrl(model.BaseUrl, out _))
            {
                errors["model.baseUrl"] = [InvalidBaseUrl];
            }

            if (string.IsNullOrWhiteSpace(model.ModelName) || model.ModelName.Trim().Length > 200)
            {
                errors["model.modelName"] = ["Enter the model name, as listed by the endpoint."];
            }

            if (model.ContextTokenBudget is { } budget
                && (budget < AssistantDefaults.MinContextTokenBudget || budget > AssistantDefaults.MaxContextTokenBudget))
            {
                errors["model.contextTokenBudget"] =
                    [$"The context budget must be between {AssistantDefaults.MinContextTokenBudget} and {AssistantDefaults.MaxContextTokenBudget} tokens."];
            }
        }

        if (request.SystemPrompt?.Length > AssistantDefaults.MaxSystemPromptLength)
        {
            errors["systemPrompt"] = [$"The system prompt can be at most {AssistantDefaults.MaxSystemPromptLength} characters."];
        }

        return errors;
    }
}
