using System.Globalization;

namespace Quorum.Web.Client;

/// <summary>What the server accepts as an attachment, so the UI can refuse the rest before uploading it.</summary>
public static class AttachmentRules
{
    public const long MaxFileBytes = 5L * 1024 * 1024;

    public static readonly IReadOnlyList<string> Extensions = [".pdf", ".docx", ".txt", ".md", ".markdown", ".csv"];

    /// <summary>The value of an <c>accept</c> attribute for a file picker.</summary>
    public static string Accept { get; } = string.Join(',', Extensions);

    /// <summary>The reason the file would be refused, or <see langword="null"/> when it can be uploaded.</summary>
    public static string? Check(string fileName, long sizeBytes)
    {
        var extension = Path.GetExtension(fileName);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return $"\"{fileName}\" is not supported. Attach a PDF, DOCX, TXT, Markdown or CSV file.";
        }

        return sizeBytes > MaxFileBytes ? $"\"{fileName}\" is larger than {MaxFileBytes / (1024 * 1024)} MB." : null;
    }

    /// <summary>A short label for the file's type, from its extension.</summary>
    public static string Kind(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "PDF",
        ".docx" => "DOCX",
        ".txt" => "TXT",
        ".md" or ".markdown" => "MD",
        ".csv" => "CSV",
        _ => "FILE",
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} MB",
    };
}
