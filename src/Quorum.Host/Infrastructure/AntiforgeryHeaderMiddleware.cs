using Quorum.Modules.Tenancy;

namespace Quorum.Host.Infrastructure;

/// <summary>
/// CSRF protection for cookie-authenticated API calls: a state-changing request carrying the auth cookie must
/// also carry the <c>X-Quorum-Request</c> header. Browsers cannot add a custom header to a cross-site request
/// without a CORS preflight, which Quorum never grants, so a forged form or link cannot pass.
/// </summary>
internal sealed class AntiforgeryHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Quorum-Request";

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetails)
    {
        var request = context.Request;
        var needsHeader =
            request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)
            && request.Cookies.ContainsKey(TenancyModule.AuthCookieName);

        if (needsHeader && !request.Headers.ContainsKey(HeaderName))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await problemDetails.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status403Forbidden,
                    Detail = $"Requests that change data must send the {HeaderName} header.",
                    Extensions = { ["code"] = "request.antiforgery_header_missing" },
                },
            });
            return;
        }

        await next(context);
    }
}
