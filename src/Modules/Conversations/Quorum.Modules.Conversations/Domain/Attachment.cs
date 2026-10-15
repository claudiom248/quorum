namespace Quorum.Modules.Conversations.Domain;

/// <summary>
/// A document attached to a conversation. Only the text extracted from it is kept (spec 002, D-3), together with the hash
/// of the uploaded bytes so the same content is not attached twice.
/// </summary>
internal sealed class Attachment
{
    public const int MaxFileNameLength = 255;

    private Attachment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public string FileName { get; private set; } = null!;

    public string MediaType { get; private set; } = null!;

    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 of the uploaded bytes, lowercase hex.</summary>
    public string ContentHash { get; private set; } = null!;

    /// <summary>The extracted text, Markdown where the format has structure.</summary>
    public string Text { get; private set; } = null!;

    public int CharacterCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Attachment Create(
        Conversation conversation,
        string fileName,
        string mediaType,
        long sizeBytes,
        string contentHash,
        string text,
        DateTimeOffset now)
    {
        return new Attachment
        {
            Id = Guid.CreateVersion7(now),
            TenantId = conversation.TenantId,
            ConversationId = conversation.Id,
            FileName = fileName,
            MediaType = mediaType,
            SizeBytes = sizeBytes,
            ContentHash = contentHash,
            Text = text,
            CharacterCount = text.Length,
            CreatedAt = now,
        };
    }
}
