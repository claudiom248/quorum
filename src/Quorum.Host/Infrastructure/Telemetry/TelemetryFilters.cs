using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Quorum.Shared.Telemetry;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Decides which HTTP requests get a span (spec 016 D-10). Static files, the UI, the version and the health endpoints never do;
/// the answer path and the upload of attachments (which feed it) always do, since a child of a request that was not sampled is not sampled either (spec 016 D-10); the other <c>/api</c> requests only with <see cref="TelemetryOptions.AdminRequests"/>.
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

        // Only the upload of an attachment, not the reads of the list, which the page may repeat.
        return adminRequests
            || AnswerPath().IsMatch(path)
            || (HttpMethods.IsPost(context.Request.Method) && AttachmentUploadPath().IsMatch(path));
    }

    [GeneratedRegex("^/api/conversations/[^/]+/(messages|regenerate|tool-calls/[^/]+/(decision|answer))/?$", RegexOptions.IgnoreCase)]
    private static partial Regex AnswerPath();

    [GeneratedRegex("^/api/conversations/[^/]+/attachments/?$", RegexOptions.IgnoreCase)]
    private static partial Regex AttachmentUploadPath();
}
