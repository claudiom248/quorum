using System.Security.Claims;
using Quorum.Modules.Tenancy.Contracts;

namespace Quorum.Host.Infrastructure;

/// <summary>
/// Authenticates requests carrying an <c>Authorization: Bearer</c> service token.
/// Skips if the user is already authenticated (cookie) or no Bearer header is present.
/// An invalid token yields 401 with no fallback to cookie authentication.
/// </summary>
internal sealed class ServiceTokenAuthenticationMiddleware(RequestDelegate next)
{
    private const string Scheme = "Bearer ";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantServiceTokenValidator validator,
        IProblemDetailsService problemDetailsService)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var token = header[Scheme.Length..].Trim();
        if (token.Length == 0)
        {
            await WriteUnauthorized(context, problemDetailsService);
            return;
        }

        var tenantId = await validator.ValidateAsync(token, context.RequestAborted);

        if (tenantId is null)
        {
            await WriteUnauthorized(context, problemDetailsService);
            return;
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(QuorumClaimTypes.TenantId, tenantId.Value.ToString()),
                new Claim(QuorumClaimTypes.ServiceToken, "true"),
            ],
            authenticationType: "ServiceToken");
        context.User = new ClaimsPrincipal(identity);

        await next(context);
    }

    private static async Task WriteUnauthorized(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = "The service token is not valid.",
                Extensions = { ["code"] = "auth.invalid_service_token" },
            },
        });
    }
}
