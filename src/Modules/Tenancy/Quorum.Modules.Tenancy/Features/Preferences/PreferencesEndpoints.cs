using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Quorum.Modules.Tenancy.Domain;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.Tenancy.Features.Preferences;

/// <summary>What a person chose for themselves, kept with their account so it follows them from one browser to another.</summary>
internal sealed class PreferencesEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var preferences = app.MapGroup("/api/me/preferences").WithTags("Preferences").RequireAuthorization();

        preferences.MapGet("/", GetAsync).WithName("GetPreferences");
        preferences.MapPut("/", UpdateAsync).WithName("UpdatePreferences");
    }

    private static async Task<Results<Ok<PreferencesResponse>, ProblemHttpResult>> GetAsync(ClaimsPrincipal principal, UserManager<QuorumUser> userManager)
    {
        if (await userManager.GetUserAsync(principal) is not { } user)
        {
            return Error.Unauthorized("auth.unknown_user", "The account does not exist any more.").ToProblem();
        }

        return TypedResults.Ok(PreferencesResponse.From(user));
    }

    /// <summary>A preference left out is kept as it is.</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        UpdatePreferencesRequest request,
        ClaimsPrincipal principal,
        UserManager<QuorumUser> userManager)
    {
        if (await userManager.GetUserAsync(principal) is not { } user)
        {
            return Error.Unauthorized("auth.unknown_user", "The account does not exist any more.").ToProblem();
        }

        if (request.LedgerStyle is not null)
        {
            var style = request.LedgerStyle.Trim().ToLowerInvariant();
            if (!LedgerStyles.All.Contains(style))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ledgerStyle"] = [$"The ledger style must be one of {string.Join(", ", LedgerStyles.All)}."],
                });
            }

            user.LedgerStyle = style == LedgerStyles.Default ? null : style;
        }

        var result = await userManager.UpdateAsync(user);
        return result.Succeeded
            ? TypedResults.NoContent()
            : Error.Conflict("preferences.not_saved", "The preferences could not be saved: try again.").ToProblem();
    }
}

/// <summary>How the tool calls of an answer are drawn: the ledger styles of the chat.</summary>
internal static class LedgerStyles
{
    public const string Default = "classic";

    public static readonly IReadOnlyList<string> All = [Default, "cards", "timeline", "bars"];
}

/// <param name="LedgerStyle"><c>classic</c>, <c>cards</c>, <c>timeline</c> or <c>bars</c>: how the tool calls of an answer are drawn.</param>
internal sealed record PreferencesResponse(string LedgerStyle)
{
    public static PreferencesResponse From(QuorumUser user) => new(user.LedgerStyle ?? LedgerStyles.Default);
}

internal sealed record UpdatePreferencesRequest(string? LedgerStyle);
