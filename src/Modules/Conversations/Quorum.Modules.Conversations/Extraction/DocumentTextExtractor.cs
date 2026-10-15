using System.Text;
using Quorum.Shared.Results;

namespace Quorum.Modules.Conversations.Extraction;

/// <summary>The text of an uploaded document, as Markdown where the format has structure, and the media type it was read as.</summary>
internal sealed record ExtractedDocument(string Text, string MediaType);

/// <summary>
/// Turns an uploaded file into text (spec 002, D-4, D-19). The format is decided by the content: a file that merely carries
/// a supported extension but holds something else is rejected. Nothing is written to disk and nothing in the file is executed.
/// </summary>
internal static class DocumentTextExtractor
{
    public const string UnsupportedTypeCode = "attachment.unsupported_type";
    public const string NoTextCode = "attachment.no_text";
    public const string UnreadableCode = "attachment.unreadable";
    public const string TimeoutCode = "attachment.extraction_timeout";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Error Unsupported = Error.UnsupportedMediaType(
        UnsupportedTypeCode,
        "This file type is not supported. Attach a PDF, DOCX, TXT, Markdown or CSV file; text files must be UTF-8.");

    private static readonly Error NoText = Error.Unprocessable(NoTextCode, "The file has no text to use.");

    /// <summary>
    /// Extracts the text, giving up with <c>attachment.extraction_timeout</c> when it takes longer than <paramref name="timeout"/>.
    /// </summary>
    public static async Task<Result<ExtractedDocument>> ExtractAsync(
        string fileName,
        ReadOnlyMemory<byte> content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var bytes = content.ToArray();

        try
        {
            return await Task.Run(() => Extract(fileName, bytes, limit.Token), limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Unprocessable(TimeoutCode, "Reading the file took too long, so it was not attached.");
        }
    }

    internal static Result<ExtractedDocument> Extract(string fileName, byte[] content, CancellationToken cancellationToken)
    {
        if (LooksBinary(content))
        {
            return Unsupported;
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        string mediaType;
        switch (extension)
        {
            case ".txt":
                mediaType = "text/plain";
                break;
            case ".md":
            case ".markdown":
                mediaType = "text/markdown";
                break;
            case ".csv":
                mediaType = "text/csv";
                break;
            default:
                return Unsupported;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Unsupported;
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        if (extension == ".csv")
        {
            text = CsvTable.ToMarkdown(text, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(text) ? NoText : new ExtractedDocument(text, mediaType);
    }

    /// <summary>Known binary signatures (PDF, zip-based formats, executables) and NUL bytes, which text never holds.</summary>
    private static bool LooksBinary(byte[] content) =>
        content.AsSpan().IndexOf((byte)0) >= 0
        || content.AsSpan().StartsWith("%PDF-"u8)
        || content.AsSpan().StartsWith("PK\u0003\u0004"u8);
}
