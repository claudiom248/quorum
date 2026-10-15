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

    private const string DocxMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static ReadOnlySpan<byte> ZipSignature => [0x50, 0x4B, 0x03, 0x04];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Error Unsupported = Error.UnsupportedMediaType(
        UnsupportedTypeCode,
        "This file type is not supported. Attach a PDF, DOCX, TXT, Markdown or CSV file; text files must be UTF-8.");

    private static readonly Error NoText = Error.Unprocessable(NoTextCode, "The file has no text to use.");

    /// <summary>
    /// Extracts the text, giving up with <c>attachment.extraction_timeout</c> when it takes longer than <paramref name="timeout"/>.
    /// </summary>
    public static Task<Result<ExtractedDocument>> ExtractAsync(
        string fileName,
        byte[] content,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunWithLimitAsync(token => Extract(fileName, content, token), timeout, cancellationToken);

    /// <summary>
    /// Runs the work off the request thread and stops waiting for it at the limit. The wait is cut with
    /// <c>WaitAsync</c>: passing the token to <c>Task.Run</c> alone would not stop work that never checks it, and the
    /// request (and the conversation lock it holds) would hang with it. An abandoned run finishes on its own.
    /// </summary>
    internal static async Task<Result<ExtractedDocument>> RunWithLimitAsync(
        Func<CancellationToken, Result<ExtractedDocument>> work,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            return await Task.Run(() => work(limit.Token), CancellationToken.None).WaitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Unprocessable(TimeoutCode, "Reading the file took too long, so it was not attached.");
        }
    }

    internal static Result<ExtractedDocument> Extract(string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // PDF and DOCX are identified by their signature: a name that promises one and a content that is another is refused.
        if (extension == ".pdf")
        {
            return content.AsSpan().StartsWith("%PDF-"u8)
                ? Document(PdfText.Extract(content, cancellationToken), "application/pdf")
                : Unsupported;
        }

        if (extension == ".docx")
        {
            return ExtractDocx(content, cancellationToken);
        }

        if (LooksBinary(content))
        {
            return Unsupported;
        }

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

    private static Result<ExtractedDocument> ExtractDocx(byte[] content, CancellationToken cancellationToken)
    {
        if (content.AsSpan().StartsWith(ZipSignature))
        {
            return Document(DocxText.Extract(content, cancellationToken), DocxMediaType);
        }

        // Password-protected Office files are OLE compound files, not zip packages (legacy .doc files are too).
        return content.AsSpan().StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 })
            ? Error.Unprocessable(UnreadableCode, "The document is password-protected or in an old format: save it as a plain DOCX.")
            : Unsupported;
    }

    private static Result<ExtractedDocument> Document(Result<string> text, string mediaType) =>
        text.IsSuccess ? new ExtractedDocument(text.Value, mediaType) : text.Error;

    /// <summary>Known binary signatures (PDF, zip-based formats, executables) and NUL bytes, which text never holds.</summary>
    private static bool LooksBinary(byte[] content) =>
        content.AsSpan().IndexOf((byte)0) >= 0
        || content.AsSpan().StartsWith("%PDF-"u8)
        || content.AsSpan().StartsWith("PK\u0003\u0004"u8);
}
