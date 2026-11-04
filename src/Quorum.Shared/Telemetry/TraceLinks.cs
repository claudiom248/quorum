namespace Quorum.Shared.Telemetry;

/// <summary>
/// The link from an audit entry to its trace (spec 016 D-21). The template is an absolute http(s) URL with the placeholder
/// <c>{traceId}</c>; a template that breaks a rule is ignored, and the rule is what the startup warning names, never the template
/// itself, since it may hold a token.
/// </summary>
public static class TraceLinks
{
    public const string Placeholder = "{traceId}";

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
    public static string? LinkFor(string? template, string? traceId)
    {
        if (string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(template) || ProblemOf(template) is not null)
        {
            return null;
        }

        return template.Trim().Replace(Placeholder, traceId, StringComparison.Ordinal);
    }
}
