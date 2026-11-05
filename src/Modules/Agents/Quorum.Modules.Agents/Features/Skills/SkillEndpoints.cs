using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Skills;

/// <summary>The skills of the tenant, managed by the administrator (spec 017 D-1, D-12, D-14, D-15).</summary>
internal sealed class SkillEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var skills = app.MapGroup("/api/skills")
            .WithTags("Skills")
            .RequireAuthorization(QuorumPolicies.SkillsManage);

        skills.MapGet("/", ListAsync).WithName("ListSkills");
        skills.MapGet("/{id:guid}", GetAsync).WithName("GetSkill");
        skills.MapPost("/", CreateAsync).WithName("CreateSkill");
        skills.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateSkill");
        skills.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteSkill");
    }

    private static async Task<Ok<List<SkillSummaryResponse>>> ListAsync(AgentsDbContext db, CancellationToken cancellationToken)
    {
        var skills = await SkillVisibility.For(db)
            .AsNoTracking()
            .OrderBy(s => s.Slug)
            .Select(s => new SkillSummaryResponse(
                s.Id,
                s.Slug,
                s.DisplayName,
                s.Description,
                s.Scope.ToString(),
                db.AgentSkills.Count(a => a.SkillId == s.Id)))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(skills);
    }

    private static async Task<Results<Ok<SkillDocument>, ProblemHttpResult>> GetAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await SkillVisibility.For(db).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } skill)
        {
            return SkillNotFound();
        }

        return TypedResults.Ok(SkillDocument.From(skill));
    }

    private static async Task<Results<CreatedAtRoute<SkillDocument>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SkillRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var input = await SkillInput.ValidateAsync(request, existing: null, db, cancellationToken);
        if (input.SlugTaken)
        {
            return SkillInput.Taken();
        }

        if (input.Errors.Count > 0)
        {
            return TypedResults.ValidationProblem(input.Errors);
        }

        var skill = Skill.Create(tenantId, input.Slug, input.DisplayName, input.Description, input.Body, input.Scope, input.ScopeTenantIds, clock.GetUtcNow());
        db.Skills.Add(skill);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return SkillInput.Taken();
        }

        return TypedResults.CreatedAtRoute(
            SkillDocument.From(skill),
            "GetSkill",
            new RouteValueDictionary { ["id"] = skill.Id });
    }

    /// <summary>Only the owner edits a skill; a Global skill of another tenant is read, not changed.</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id,
        SkillRequest request,
        AgentsDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await db.Skills.SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } skill)
        {
            return SkillNotFound();
        }

        var input = await SkillInput.ValidateAsync(request, skill, db, cancellationToken);
        if (input.SlugTaken)
        {
            return SkillInput.Taken();
        }

        if (input.Errors.Count > 0)
        {
            return TypedResults.ValidationProblem(input.Errors);
        }

        // Narrowing the scope must not leave an agent that holds the skill without it (D-13).
        if (await SkillScopeRules.AgentsBrokenByAsync(db, skill.Id, input.Scope, input.ScopeTenantIds, cancellationToken) is { Count: > 0 } broken)
        {
            return BlockingReference.Conflict(
                "skill.scope_in_use",
                "The new scope would hide this skill from agents that use it. Change them first.",
                broken);
        }

        skill.Revise(input.Slug, input.DisplayName, input.Description, input.Body, input.Scope, input.ScopeTenantIds, clock.GetUtcNow());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return SkillInput.Taken();
        }

        return TypedResults.NoContent();
    }

    /// <summary>Refused while an agent holds the skill (spec 017 D-14); an unassigned skill is simply removed.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Skills.SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } skill)
        {
            return SkillNotFound();
        }

        if (await SkillScopeRules.HoldersOfAsync(db, skill.Id, cancellationToken) is { Count: > 0 } holders)
        {
            return InUse(holders);
        }

        db.Skills.Remove(skill);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsForeignKeyViolation())
        {
            // An agent took the skill after the check above.
            db.ChangeTracker.Clear();
            return InUse(await SkillScopeRules.HoldersOfAsync(db, id, cancellationToken));
        }

        return TypedResults.NoContent();
    }

    private static ProblemHttpResult SkillNotFound() => Error.NotFound("skill.not_found", "The skill does not exist.").ToProblem();

    private static ProblemHttpResult InUse(IReadOnlyList<BlockingReference> holders) =>
        BlockingReference.Conflict("skill.in_use", "The skill is assigned to agents. Remove it from them first.", holders);
}

internal sealed record SkillSummaryResponse(Guid Id, string Slug, string DisplayName, string Description, string Scope, int AgentCount);

internal sealed record SkillDocument(
    Guid Id,
    string Slug,
    string DisplayName,
    string Description,
    string Body,
    string Scope,
    List<Guid> ScopeTenantIds)
{
    public static SkillDocument From(Skill skill) => new(
        skill.Id,
        skill.Slug,
        skill.DisplayName,
        skill.Description,
        skill.Body,
        skill.Scope.ToString(),
        skill.ScopeTenantIds);
}
