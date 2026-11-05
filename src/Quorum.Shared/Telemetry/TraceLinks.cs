using System.Globalization;

namespace Quorum.Shared.Telemetry;

/// <summary>
/// The link from an audit entry to its trace (spec 016 D-21). The template is an absolute http(s) URL with the placeholder
/// <c>{traceId}</c>; a template that breaks a rule is ignored, and the rule is what the startup warning names, never the template
/// itself, since it may hold a token. Two more placeholders are filled when the template has them: <c>{from}</c> and <c>{to}</c>,
/// the start and the end of a window around the call, as Unix time in microseconds, for the backends whose trace page needs the
/// time range (OpenObserve does).
/// </summary>
public static class TraceLinks
{
    public const string Placeholder = "{traceId}";

    /// <summary>
    /// How far the window of <c>{from}</c> and <c>{to}</c> reaches before the start and after the end of the call: the trace holds the
    /// whole answer, which began before the call and ends after it, and a confirmation can make it long.
    /// </summary>
    public static readonly TimeSpan WindowMargin = TimeSpan.FromHours(1);

    /// <summary>The rule the template breaks, or <see langword="null"/> when it is usable or not set at all.</summary>
    public static string? ProblemOf(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var value = template.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "is not an absolute URL";
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return "does not use the http or https scheme";
        }

        return value.Contains(Placeholder, StringComparison.Ordinal) ? null : "has no {traceId} placeholder";
    }

    /// <summary>The link of a trace, or <see langword="null"/> without a trace id or with a template that is not usable.</summary>
    /// <param name="startedAt">When the call started: the window of <c>{from}</c> and <c>{to}</c> is built around it.</param>
    /// <param name="completedAt">When the call ended; a call that never completed has the window around its start.</param>
    public static string? LinkFor(string? template, string? traceId, DateTimeOffset startedAt, DateTimeOffset? completedAt)
    {
        if (string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(template) || ProblemOf(template) is not null)
        {
            return null;
        }

        return template.Trim()
            .Replace(Placeholder, traceId, StringComparison.Ordinal)
            .Replace("{from}", Microseconds(startedAt - WindowMargin), StringComparison.Ordinal)
            .Replace("{to}", Microseconds((completedAt ?? startedAt) + WindowMargin), StringComparison.Ordinal);
    }

    private static string Microseconds(DateTimeOffset moment) =>
        ((moment.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10).ToString(CultureInfo.InvariantCulture);
}
