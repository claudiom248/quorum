using Microsoft.AspNetCore.StaticFiles;

namespace Quorum.Host.Infrastructure;

/// <summary>
/// The preview viewer (spec 014, D-7) runs in an iframe sandboxed without <c>allow-same-origin</c>, so its origin is
/// opaque and <c>'self'</c> would match nothing: the policy names the app's own origin, taken from the request, and
/// <c>/preview/</c> is where the viewer and its vendored libraries live. There is no <c>connect-src</c>, so the viewer
/// cannot reach the network, and <c>frame-src about:</c> lets the nested <c>srcdoc</c> frame exist while blocking its
/// navigation to any real address.
/// </summary>
internal static class PreviewSecurityHeaders
{
    public static void Apply(StaticFileResponseContext context)
    {
        var request = context.Context.Request;
        if (!request.Path.StartsWithSegments("/preview"))
        {
            return;
        }

        var origin = $"{request.Scheme}://{request.Host}";
        context.Context.Response.Headers.ContentSecurityPolicy = string.Join("; ",
            "default-src 'none'",
            $"script-src {origin}/preview/",
            $"style-src {origin}/preview/ 'unsafe-inline'",
            "img-src data:",
            "font-src data:",
            "form-action 'none'",
            "base-uri 'none'",
            "frame-src about:");
    }
}
