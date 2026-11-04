using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Decides which HTTP requests get a span (spec 016 D-10). Static files, the UI, the version and the health endpoints never do;
/// the answer path always does; the other <c>/api</c> requests only with <see cref="TelemetryOptions.AdminRequests"/>.
/// </summary>
internal static partial class TelemetryFilters
{
    public static bool ShouldTrace(HttpContext context, bool adminRequests)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/version", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return adminRequests || AnswerPath().IsMatch(path);
    }

    [GeneratedRegex("^/api/conversations/[^/]+/(messages|regenerate|tool-calls/[^/]+/(decision|answer))/?$", RegexOptions.IgnoreCase)]
    private static partial Regex AnswerPath();
}
