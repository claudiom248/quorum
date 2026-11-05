using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Features.Skills.Import;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Skills;

/// <summary>
/// Importing skills in two steps (spec 017 D-19 to D-24): a preview of every skill found, then the confirmation with a decision per
/// conflict. Nothing is written before the confirmation.
/// </summary>
internal sealed partial class SkillImportEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var skills = app.MapGroup("/api/skills")
            .WithTags("Skills")
            .RequireAuthorization(QuorumPolicies.SkillsManage);

        skills.MapPost("/import/preview", PreviewAsync).DisableAntiforgery().WithName("PreviewSkillImport");
        skills.MapPost("/import", ConfirmAsync).WithName("ConfirmSkillImport");
    }

    private static async Task<Results<Ok<SkillImportPreviewResponse>, ProblemHttpResult>> PreviewAsync(
        IFormFileCollection files,
        AgentsDbContext db,
        ITenantContext tenant,
        SkillImportPreviews previews,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var streams = files.Select(file => (file.FileName, file.OpenReadStream(), file.Length)).ToList();
        IReadOnlyList<PackageFile> packageFiles;
        try
        {
            packageFiles = SkillPackageReader.Read(streams);
        }
        catch (SkillPackageException exception)
        {
            return TypedResults.Problem(detail: exception.Message, statusCode: exception.StatusCode);
        }
        finally
        {
            foreach (var (_, stream, _) in streams)
            {
                await stream.DisposeAsync();
            }
        }

        var existing = await db.Skills.AsNoTracking().Select(s => s.Slug).ToHashSetAsync(StringComparer.Ordinal, cancellationToken);
        var candidates = SkillFileDiscovery.Discover(packageFiles, existing.Contains);
        if (candidates.Count == 0)
        {
            return TypedResults.Problem(detail: "No SKILL.md file was found: a skill is a folder, at most two levels deep, with a SKILL.md in it.", statusCode: StatusCodes.Status400BadRequest);
        }

        var preview = previews.Store(tenantId, candidates);
        return TypedResults.Ok(new SkillImportPreviewResponse(
            preview.Id,
            preview.ExpiresAt,
            candidates.Select(c => new SkillImportItem(c.Slug, c.Status.ToString().ToLowerInvariant(), c.Reason, c.IgnoredFiles, c.IgnoredFields)).ToList()));
    }

    /// <summary>Writes what the preview allows. A skill that another one took the slug of after the preview fails alone (D-20, AC-27).</summary>
    private static async Task<Results<Ok<SkillImportResultResponse>, ProblemHttpResult>> ConfirmAsync(
        SkillImportConfirmRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        SkillImportPreviews previews,
        TimeProvider clock,
        ILogger<SkillImportEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        // The decisions are checked before the preview is taken: a typo must not burn the preview.
        var decisions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var decision in request.Decisions ?? [])
        {
            if (decision.Action is not ("skip" or "overwrite"))
            {
                return TypedResults.Problem(detail: $"The action of '{decision.Slug}' must be skip or overwrite.", statusCode: StatusCodes.Status400BadRequest);
            }

            decisions[decision.Slug] = decision.Action;
        }

        var preview = previews.Take(tenantId, request.PreviewId);
        if (preview is null)
        {
            return TypedResults.Problem(detail: "The preview does not exist or has expired. Upload the skills again.", statusCode: StatusCodes.Status404NotFound);
        }

        var style = await db.SkillSettings.AsNoTracking().Select(s => (SkillDisplayNameStyle?)s.DisplayNameStyle).SingleOrDefaultAsync(cancellationToken)
            ?? SkillDisplayNameStyle.Slug;

        var created = new List<string>();
        var overwritten = new List<string>();
        var skipped = new List<string>();
        var failed = new List<SkillImportFailure>();
        foreach (var candidate in preview.Candidates.Where(c => c.Status != ImportStatus.Invalid))
        {
            var overwrite = decisions.GetValueOrDefault(candidate.Slug) == "overwrite";
            var now = clock.GetUtcNow();

            if (candidate.Status == ImportStatus.Conflict && await db.Skills.SingleOrDefaultAsync(s => s.Slug == candidate.Slug, cancellationToken) is { } current)
            {
                if (!overwrite)
                {
                    skipped.Add(candidate.Slug);
                    continue;
                }

                // Overwrite replaces the description and the body only: name, scope and assignments stay (D-24).
                current.Revise(current.Slug, current.DisplayName, candidate.Description, candidate.Body, current.Scope, current.ScopeTenantIds, now);
                await db.SaveChangesAsync(cancellationToken);
                overwritten.Add(candidate.Slug);
                continue;
            }

            if (await db.Skills.AnyAsync(s => s.Slug == candidate.Slug, cancellationToken))
            {
                failed.Add(new SkillImportFailure(candidate.Slug, "A skill with this slug was created after the preview."));
                LogImportSkillFailed(logger, candidate.Slug, "slug taken after the preview");
                continue;
            }

            var displayName = style == SkillDisplayNameStyle.Humanized ? Humanize(candidate.Slug) : candidate.Slug;
            db.Skills.Add(Skill.Create(tenantId, candidate.Slug, displayName, candidate.Description, candidate.Body, AgentScope.Tenant, [], now));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                created.Add(candidate.Slug);
            }
            catch (DbUpdateException exception) when (exception.IsUniqueViolation())
            {
                // Another request took the slug between the check and the save: this skill fails, the others go on.
                db.ChangeTracker.Clear();
                failed.Add(new SkillImportFailure(candidate.Slug, "A skill with this slug was created after the preview."));
                LogImportSkillFailed(logger, candidate.Slug, "slug taken between the check and the save");
            }
        }

        LogImportConfirmed(logger, created.Count, overwritten.Count, skipped.Count, failed.Count);
        return TypedResults.Ok(new SkillImportResultResponse(created, overwritten, skipped, failed));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Skill import confirmed: {Created} created, {Overwritten} overwritten, {Skipped} skipped, {Failed} failed.")]
    private static partial void LogImportConfirmed(ILogger logger, int created, int overwritten, int skipped, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skill import failed for the skill {Skill}: {Reason}.")]
    private static partial void LogImportSkillFailed(ILogger logger, string skill, string reason);

    /// <summary>The name a person reads: <c>code-review</c> becomes <c>Code review</c> (D-28).</summary>
    internal static string Humanize(string slug)
    {
        var words = slug.Replace('-', ' ');
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }
}

internal sealed record SkillImportPreviewResponse(Guid PreviewId, DateTimeOffset ExpiresAt, List<SkillImportItem> Items);

internal sealed record SkillImportItem(string Slug, string Status, string? Reason, IReadOnlyList<string> IgnoredFiles, IReadOnlyList<string> IgnoredFields);

internal sealed record SkillImportConfirmRequest(Guid PreviewId, List<SkillImportDecision>? Decisions);

internal sealed record SkillImportDecision(string Slug, string Action);

internal sealed record SkillImportResultResponse(List<string> Created, List<string> Overwritten, List<string> Skipped, List<SkillImportFailure> Failed);

internal sealed record SkillImportFailure(string Slug, string Reason);
