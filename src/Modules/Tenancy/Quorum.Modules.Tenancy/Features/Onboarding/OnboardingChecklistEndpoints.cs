using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Tenancy.Domain;
using Quorum.Modules.Tenancy.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.Tenancy.Features.Onboarding;

internal sealed class OnboardingChecklistEndpoints : IEndpointGroup
{
    private static readonly (string Key, ChecklistSteps Step)[] Steps =
    [
        ("model", ChecklistSteps.Model),
        ("prompt", ChecklistSteps.Prompt),
        ("tools", ChecklistSteps.Tools),
        ("knowledge", ChecklistSteps.Knowledge),
    ];

    private static readonly Error UnknownStep =
        Error.NotFound("onboarding.unknown_step", "The checklist has no such step: use model, prompt, tools or knowledge.");

    public void Map(IEndpointRouteBuilder app)
    {
        var checklist = app.MapGroup("/api/onboarding/checklist")
            .WithTags("Onboarding")
            .RequireAuthorization(TenancyModule.AdminPolicy);

        checklist.MapGet("", GetAsync).WithName("GetOnboardingChecklist");
        checklist.MapPut("/dismissed", (TenancyDbContext db, TimeProvider clock, CancellationToken ct) => SetDismissedAsync(true, db, clock, ct))
            .WithName("DismissOnboardingChecklist");
        checklist.MapDelete("/dismissed", (TenancyDbContext db, TimeProvider clock, CancellationToken ct) => SetDismissedAsync(false, db, clock, ct))
            .WithName("RestoreOnboardingChecklist");
        checklist.MapPut("/{key}", CompleteStepAsync).WithName("CompleteOnboardingStep");
    }

    private static async Task<Ok<ChecklistResponse>> GetAsync(TenancyDbContext db, CancellationToken cancellationToken)
    {
        // An installation set up before the checklist existed has no row yet: its model step is done by definition.
        var row = await db.OnboardingChecklists.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var completed = row?.Completed ?? ChecklistSteps.Model;

        return TypedResults.Ok(new ChecklistResponse(
            [.. Steps.Select(s => new ChecklistItemResponse(s.Key, completed.HasFlag(s.Step)))],
            row?.Dismissed ?? false));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> CompleteStepAsync(
        string key,
        TenancyDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var match = Steps.Where(s => s.Key == key).Select(s => (ChecklistSteps?)s.Step).SingleOrDefault();
        if (match is not { } step)
        {
            return UnknownStep.ToProblem();
        }

        var now = clock.GetUtcNow();
        await EnsureRowAsync(db, now, cancellationToken);
        await db.OnboardingChecklists.ExecuteUpdateAsync(
            set => set.SetProperty(c => c.Completed, c => c.Completed | step).SetProperty(c => c.UpdatedAt, now),
            cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetDismissedAsync(bool dismissed, TenancyDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await EnsureRowAsync(db, now, cancellationToken);
        await db.OnboardingChecklists.ExecuteUpdateAsync(
            set => set.SetProperty(c => c.Dismissed, dismissed).SetProperty(c => c.UpdatedAt, now),
            cancellationToken);

        return TypedResults.NoContent();
    }

    private static Task<int> EnsureRowAsync(TenancyDbContext db, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO tenancy.onboarding_checklist ("Id", "Completed", "Dismissed", "UpdatedAt")
            VALUES ({OnboardingChecklist.SingletonId}, {(int)ChecklistSteps.Model}, FALSE, {now})
            ON CONFLICT ("Id") DO NOTHING
            """,
            cancellationToken);
}

internal sealed record ChecklistResponse(ChecklistItemResponse[] Items, bool Dismissed);

internal sealed record ChecklistItemResponse(string Key, bool Done);
