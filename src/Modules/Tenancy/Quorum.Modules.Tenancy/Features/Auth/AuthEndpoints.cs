using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Quorum.Modules.Tenancy.Contracts;
using Quorum.Modules.Tenancy.Domain;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.Tenancy.Features.Auth;

internal sealed class AuthEndpoints : IEndpointGroup
{
    private static readonly Error InvalidCredentials =
        Error.Unauthorized("auth.invalid_credentials", "The email or the password is wrong.");

    private static readonly Error LockedOut =
        Error.Locked("auth.locked_out", "Too many failed attempts: the account is locked for a few minutes.");

    public void Map(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/login", LoginAsync).WithName("Login");
        auth.MapPost("/logout", LogoutAsync).WithName("Logout");
        auth.MapGet("/me", GetMe).WithName("GetMe").RequireAuthorization();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        UserManager<QuorumUser> userManager,
        SignInManager<QuorumUser> signInManager)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password)
            || await userManager.FindByEmailAsync(request.Email.Trim()) is not { } user)
        {
            return InvalidCredentials.ToProblem();
        }

        var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return TypedResults.NoContent();
        }

        return (result.IsLockedOut ? LockedOut : InvalidCredentials).ToProblem();
    }

    private static async Task<NoContent> LogoutAsync(SignInManager<QuorumUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static Ok<MeResponse> GetMe(ClaimsPrincipal user) =>
        TypedResults.Ok(new MeResponse(
            user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            user.FindFirstValue(QuorumClaimTypes.DisplayName) ?? string.Empty,
            user.FindFirstValue(ClaimTypes.Role) ?? string.Empty));
}

internal sealed record LoginRequest(string? Email, string? Password);

internal sealed record MeResponse(string Email, string DisplayName, string Role);
