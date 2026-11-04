namespace Quorum.Web.Client;

/// <summary>The content kinds the preview panel renders (spec 014, D-1).</summary>
public enum PreviewKind
{
    Html,
    Svg,
    Json,
    Csv,
    Mermaid,
}

/// <summary>
/// Which fenced code blocks get a Preview button and what the panel calls them. Nothing is detected from the
/// content: only the info string decides (D-1).
/// </summary>
public static class PreviewKinds
{
    /// <summary>
    /// The kind a fenced code block's info string names, or <see langword="null"/> when the tag is missing or not one
    /// of the supported ones. <c>htm</c> is treated as <c>html</c> and <c>mmd</c> as <c>mermaid</c>; anything after
    /// the first word of the info string (attributes a dialect may add) is ignored.
    /// </summary>
    public static PreviewKind? FromInfoString(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }

        var tag = info.AsSpan().Trim();
        var end = tag.IndexOfAny(' ', '\t');
        if (end >= 0)
        {
            tag = tag[..end];
        }

        if (tag.Equals("html", StringComparison.OrdinalIgnoreCase) || tag.Equals("htm", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Html;
        }

        if (tag.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Svg;
        }

        if (tag.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Json;
        }

        if (tag.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Csv;
        }

        if (tag.Equals("mermaid", StringComparison.OrdinalIgnoreCase) || tag.Equals("mmd", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.Mermaid;
        }

        return null;
    }

    /// <summary>The name the panel bar shows (D-4).</summary>
    public static string Label(PreviewKind kind) => kind switch
    {
        PreviewKind.Html => "HTML",
        PreviewKind.Svg => "SVG",
        PreviewKind.Json => "JSON",
        PreviewKind.Csv => "CSV",
        PreviewKind.Mermaid => "Mermaid",
        _ => kind.ToString(),
    };

    /// <summary>The file name extension Download uses (D-4): <c>preview.html</c>, <c>.svg</c>, <c>.json</c>, <c>.csv</c> or <c>.mmd</c>.</summary>
    public static string Extension(PreviewKind kind) => kind switch
    {
        PreviewKind.Html => "html",
        PreviewKind.Svg => "svg",
        PreviewKind.Json => "json",
        PreviewKind.Csv => "csv",
        PreviewKind.Mermaid => "mmd",
        _ => "txt",
    };

    /// <summary>The value of the button's <c>data-preview-kind</c> attribute; the click handler parses it back with <see cref="Parse"/>.</summary>
    public static string DataValue(PreviewKind kind) => kind switch
    {
        PreviewKind.Html => "html",
        PreviewKind.Svg => "svg",
        PreviewKind.Json => "json",
        PreviewKind.Csv => "csv",
        PreviewKind.Mermaid => "mermaid",
        _ => kind.ToString().ToLowerInvariant(),
    };

    public static PreviewKind? Parse(string? value) =>
        Enum.TryParse<PreviewKind>(value, ignoreCase: true, out var kind) ? kind : null;
}
