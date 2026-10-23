using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Tenancy.Domain;
using Quorum.Modules.Tenancy.Persistence;
using Quorum.Modules.Tenancy.Setup;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.Tenancy.Features.Setup;

/// <summary>
/// Creates the default tenant, the administrator and the default assistant in one platform transaction,
/// closes setup mode and signs the administrator in.
/// </summary>
internal static class CompleteSetup
{
    public static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> HandleAsync(
        CompleteSetupRequest request,
        SetupMode setupMode,
        IPlatformUnitOfWork unitOfWork,
        TenancyDbContext db,
        UserManager<QuorumUser> userManager,
        SignInManager<QuorumUser> signInManager,
        IAssistantProvisioning assistantProvisioning,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var errors = SetupValidation.Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var adminRequest = request.Admin!;
        var modelRequest = request.Model!;
        var now = clock.GetUtcNow();
        QuorumUser admin;

        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                var tenant = Tenant.CreateDefault(now);
                db.Tenants.Add(tenant);
                db.SetupStates.Add(SetupState.Completed(now));
                db.OnboardingChecklists.Add(OnboardingChecklist.Start(now));
                await db.SaveChangesAsync(cancellationToken);

                var email = adminRequest.Email!.Trim();
                admin = new QuorumUser
                {
                    Id = Guid.CreateVersion7(now),
                    TenantId = tenant.Id,
                    UserName = email,
                    Email = email,
                    DisplayName = adminRequest.DisplayName!.Trim(),
                    Role = Roles.Admin,
                    CreatedAt = now,
                    LockoutEnabled = true,
                };

                var created = await userManager.CreateAsync(admin, adminRequest.Password!);
                if (!created.Succeeded)
                {
                    return TypedResults.ValidationProblem(ToValidationErrors(created.Errors));
                }

                await assistantProvisioning.CreateDefaultAsync(
                    tenant.Id,
                    new DefaultAssistantRequest(
                        new Uri(modelRequest.BaseUrl!.Trim()),
                        modelRequest.ModelName!.Trim(),
                        string.IsNullOrWhiteSpace(modelRequest.ApiKey) ? null : modelRequest.ApiKey.Trim(),
                        modelRequest.ContextTokenBudget ?? AssistantDefaults.ContextTokenBudget,
                        string.IsNullOrWhiteSpace(request.SystemPrompt) ? AssistantDefaults.SystemPrompt : request.SystemPrompt.Trim(),
                        modelRequest.SupportsTools ?? true),
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.IsUniqueViolation())
            {
                return SetupErrors.AlreadyCompleted.ToProblem();
            }
        }

        setupMode.Complete();
        await signInManager.SignInAsync(admin, isPersistent: false);

        return TypedResults.NoContent();
    }

    private static Dictionary<string, string[]> ToValidationErrors(IEnumerable<IdentityError> errors) =>
        errors
            .GroupBy(error => error.Code.StartsWith("Password", StringComparison.Ordinal) ? "admin.password" : "admin.email")
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray());
}

internal sealed record CompleteSetupRequest(AdminRequest? Admin, ModelRequest? Model, string? SystemPrompt);

internal sealed record AdminRequest(string? Email, string? DisplayName, string? Password);

internal sealed record ModelRequest(string? BaseUrl, string? ModelName, string? ApiKey, int? ContextTokenBudget, bool? SupportsTools);
