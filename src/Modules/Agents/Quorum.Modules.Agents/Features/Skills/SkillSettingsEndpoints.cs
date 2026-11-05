using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Skills;

/// <summary>The display name style of the tenant's imported skills (spec 017 D-28). Absent row means <c>Slug</c>.</summary>
internal sealed class SkillSettingsEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/skills/settings")
            .WithTags("Skills")
            .RequireAuthorization(QuorumPolicies.SkillsManage);

        settings.MapGet("/", GetAsync).WithName("GetSkillSettings");
        settings.MapPut("/", SetAsync).WithName("SetSkillSettings");
    }

    private static async Task<Ok<SkillSettingsResponse>> GetAsync(AgentsDbContext db, CancellationToken cancellationToken)
    {
        var style = await db.SkillSettings.AsNoTracking()
            .Select(s => (SkillDisplayNameStyle?)s.DisplayNameStyle)
            .SingleOrDefaultAsync(cancellationToken) ?? SkillDisplayNameStyle.Slug;

        return TypedResults.Ok(new SkillSettingsResponse(style.ToString()));
    }

    private static async Task<Results<NoContent, ValidationProblem>> SetAsync(
        SkillSettingsRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        if (!Enum.TryParse(request.DisplayNameStyle, ignoreCase: true, out SkillDisplayNameStyle style) || !Enum.IsDefined(style))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayNameStyle"] = ["The display name style must be Slug or Humanized."],
            });
        }

        if (await db.SkillSettings.SingleOrDefaultAsync(cancellationToken) is { } current)
        {
            current.ChangeDisplayNameStyle(style);
        }
        else
        {
            db.SkillSettings.Add(SkillSettings.Create(tenantId, style));
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

internal sealed record SkillSettingsResponse(string DisplayNameStyle);

internal sealed record SkillSettingsRequest(string? DisplayNameStyle);
