using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Features.Skills;

/// <summary>
/// What an administrator sent for a skill, checked and trimmed: <see cref="Errors"/> says what is wrong when it cannot be saved.
/// The rules are those of spec 017: slug (D-12), description and body (D-11), scope (D-13). Nothing is normalized silently.
/// </summary>
internal sealed record SkillInput(
    Dictionary<string, string[]> Errors,
    bool SlugTaken,
    string Slug,
    string DisplayName,
    string Description,
    string Body,
    AgentScope Scope,
    List<Guid> ScopeTenantIds)
{
    public static ProblemHttpResult Taken() =>
        TypedResults.Problem(
            detail: "Another skill already has this slug.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = "skill.slug_taken" });

    /// <param name="existing">The skill being replaced; <see langword="null"/> when creating.</param>
    public static async Task<SkillInput> ValidateAsync(SkillRequest request, Skill? existing, AgentsDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);

        var errors = new Dictionary<string, string[]>();

        var slug = request.Slug?.Trim() ?? string.Empty;
        var taken = false;
        if (!SkillRules.IsValidSlug(slug))
        {
            errors["slug"] = [$"The slug must be 1 to {AgentRules.MaxSlugLength} characters: lower-case letters, digits and '-', starting with a letter or digit."];
        }
        else
        {
            var ownId = existing?.Id ?? Guid.Empty;
            taken = await db.Skills.AnyAsync(s => s.Slug == slug && s.Id != ownId, cancellationToken);
        }

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > SkillRules.MaxDisplayNameLength)
        {
            errors["displayName"] = [$"The display name must be 1 to {SkillRules.MaxDisplayNameLength} characters."];
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (!SkillRules.IsValidDescription(description))
        {
            errors["description"] = [$"The description, which tells the model when to use this skill, must be 1 to {SkillRules.MaxDescriptionLength} characters."];
        }

        var body = request.Body ?? string.Empty;
        if (!SkillRules.IsValidBody(body))
        {
            errors["body"] = ["The body is required."];
        }

        var scope = AgentScope.Tenant;
        var tenantIds = (request.ScopeTenantIds ?? []).Distinct().ToList();
        if (!Enum.TryParse(request.Scope, ignoreCase: true, out scope) || !Enum.IsDefined(scope) || int.TryParse(request.Scope, out _))
        {
            errors["scope"] = ["The scope must be Global or Tenant."];
        }
        else if (scope == AgentScope.Global && tenantIds.Count > 0)
        {
            errors["scopeTenantIds"] = ["A global scope has no tenants."];
        }
        else if (tenantIds.Contains(Guid.Empty))
        {
            errors["scopeTenantIds"] = ["A tenant id in the scope is empty."];
        }

        return new SkillInput(errors, taken, slug, displayName, description, body, scope, tenantIds);
    }
}

/// <summary>A skill as the administrator writes it (spec 017 API contract).</summary>
internal sealed record SkillRequest(
    string? Slug,
    string? DisplayName,
    string? Description,
    string? Body,
    string? Scope,
    List<Guid>? ScopeTenantIds);
