using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>The limits of the tool-calling loop and the audit retention, editable by the administrator.</summary>
internal sealed class ToolSettingsEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings/tools")
            .WithTags("Settings")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        settings.MapGet("/", GetAsync).WithName("GetToolSettings");
        settings.MapPut("/", UpdateAsync).WithName("UpdateToolSettings");
    }

    private static async Task<Ok<ToolSettingsDto>> GetAsync(AgentsDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var settings = await db.ToolSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            ?? ToolSettings.CreateDefault(tenant.TenantId!.Value);
        return TypedResults.Ok(ToolSettingsDto.From(settings));
    }

    /// <param name="confirmDeletion">
    /// A retention shorter than the age of some entries deletes them for good at the next purge, so it is refused (409, with their
    /// count) unless the caller confirms it knows.
    /// </param>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        ToolSettingsDto request,
        AgentsDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CancellationToken cancellationToken,
        bool confirmDeletion = false)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (!confirmDeletion)
        {
            // The purge keeps an entry exactly at the limit and deletes older ones: count the same.
            var cutoff = clock.GetUtcNow().AddDays(-request.AuditRetentionDays);
            var wouldDelete = await db.ToolAuditEntries.CountAsync(e => e.StartedAt < cutoff, cancellationToken);
            if (wouldDelete > 0)
            {
                return Error.Conflict(
                    "tool.retention_would_delete",
                    $"A retention of {request.AuditRetentionDays} days would delete {wouldDelete} audit entries for good at the next purge. " +
                    "Repeat the request with confirmDeletion=true to go ahead.").ToProblem();
            }
        }

        var settings = await db.ToolSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = ToolSettings.CreateDefault(tenant.TenantId!.Value);
            db.ToolSettings.Add(settings);
        }

        settings.Change(
            request.MaxRounds,
            request.TotalTimeoutSeconds,
            request.CallTimeoutSeconds,
            request.ConfirmationTimeoutSeconds,
            request.MaxResultCharacters,
            request.AuditRetentionDays);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static Dictionary<string, string[]> Validate(ToolSettingsDto request)
    {
        var errors = new Dictionary<string, string[]>();
        Check(errors, "maxRounds", request.MaxRounds, 1, 32);
        Check(errors, "totalTimeoutSeconds", request.TotalTimeoutSeconds, 10, 3600);
        Check(errors, "callTimeoutSeconds", request.CallTimeoutSeconds, 1, 600);
        Check(errors, "confirmationTimeoutSeconds", request.ConfirmationTimeoutSeconds, 10, 3600);
        Check(errors, "maxResultCharacters", request.MaxResultCharacters, 1000, 200_000);
        Check(errors, "auditRetentionDays", request.AuditRetentionDays, 1, 3650);
        return errors;
    }

    private static void Check(Dictionary<string, string[]> errors, string field, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors[field] = [$"The value must be between {min} and {max}."];
        }
    }
}

internal sealed record ToolSettingsDto(
    int MaxRounds,
    int TotalTimeoutSeconds,
    int CallTimeoutSeconds,
    int ConfirmationTimeoutSeconds,
    int MaxResultCharacters,
    int AuditRetentionDays)
{
    public static ToolSettingsDto From(ToolSettings settings) => new(
        settings.MaxRounds,
        settings.TotalTimeoutSeconds,
        settings.CallTimeoutSeconds,
        settings.ConfirmationTimeoutSeconds,
        settings.MaxResultCharacters,
        settings.AuditRetentionDays);
}
