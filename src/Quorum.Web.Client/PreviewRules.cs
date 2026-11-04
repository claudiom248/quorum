using System.Globalization;
using System.Text;

namespace Quorum.Web.Client;

/// <summary>The limits, names and labels of the preview panel (spec 014, D-4 and D-16).</summary>
public static class PreviewRules
{
    /// <summary>A block above 1 MB of text is not rendered (D-16).</summary>
    public const int MaxTextBytes = 1_000_000;

    /// <summary>The notice the panel opens with instead of the render, or <see langword="null"/> when the content is within the limit.</summary>
    public static string? LimitNotice(PreviewContent content)
    {
        var bytes = Encoding.UTF8.GetByteCount(content.Text);
        return bytes > MaxTextBytes
            ? $"This block is {FormatSize(bytes)}; the preview limit is {FormatSize(MaxTextBytes)}."
            : null;
    }

    /// <summary>The bar title: the kind and the size, for instance "JSON · 3.2 KB", unless the producer named the content.</summary>
    public static string Title(PreviewContent content) =>
        content.Title is { Length: > 0 } title
            ? title
            : $"{PreviewKinds.Label(content.Kind)} · {FormatSize(Encoding.UTF8.GetByteCount(content.Text))}";

    /// <summary>Download saves the raw text as <c>preview.&lt;ext&gt;</c> (D-4).</summary>
    public static string DownloadName(PreviewKind kind) => $"preview.{PreviewKinds.Extension(kind)}";

    /// <summary>Always with a dot decimal: the labels of the panel do not follow the browser's locale.</summary>
    public static string FormatSize(int bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB"),
    };
}
