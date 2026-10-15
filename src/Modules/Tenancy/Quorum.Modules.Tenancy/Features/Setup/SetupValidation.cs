using System.Net.Mail;
using Quorum.Modules.Agents.Contracts;

namespace Quorum.Modules.Tenancy.Features.Setup;

internal static class SetupValidation
{
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
            if (!ModelEndpointRules.TryParseBaseUrl(model.BaseUrl, out _))
            {
                errors["model.baseUrl"] = [ModelEndpointRules.InvalidBaseUrl];
            }

            if (!ModelEndpointRules.IsValidModelName(model.ModelName))
            {
                errors["model.modelName"] = [ModelEndpointRules.InvalidModelName];
            }

            if (model.ContextTokenBudget is { } budget && !ModelEndpointRules.IsValidContextTokenBudget(budget))
            {
                errors["model.contextTokenBudget"] = [ModelEndpointRules.InvalidContextTokenBudget];
            }
        }

        if (!ModelEndpointRules.IsValidSystemPrompt(request.SystemPrompt))
        {
            errors["systemPrompt"] = [ModelEndpointRules.InvalidSystemPrompt];
        }

        return errors;
    }
}
