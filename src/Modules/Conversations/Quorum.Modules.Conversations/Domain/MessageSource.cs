namespace Quorum.Modules.Conversations.Domain;

/// <summary>
/// A source an answer cited, kept with the message (spec 003 D-15): references only, never the text of the passage. The knowledge
/// base is stored by name, not by id, so the copy stays as it was when the answer was given even if the knowledge base is renamed
/// or deleted (D-17).
/// </summary>
internal sealed class MessageSource
{
    public const int MaxKnowledgeBaseLength = 100;
    public const int MaxReferenceLength = 300;
    public const int MaxUrlLength = 2000;

    private MessageSource()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid MessageId { get; private set; }

    /// <summary>The number the answer cites it by, <c>[n]</c>.</summary>
    public int N { get; private set; }

    public string KnowledgeBase { get; private set; } = null!;

    public string? DocumentId { get; private set; }

    public string? Title { get; private set; }

    public string? Position { get; private set; }

    public string? Url { get; private set; }

    /// <summary>
    /// The values were limited when they were read from the store; they are limited again here so that an answer is never lost
    /// to a column that is too short. A URL that does not fit is dropped, not cut: a cut URL would point somewhere else.
    /// </summary>
    public static MessageSource Create(Message message, int n, string knowledgeBase, string? documentId, string? title, string? position, string? url, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = message.TenantId,
        MessageId = message.Id,
        N = n,
        KnowledgeBase = Cut(knowledgeBase, MaxKnowledgeBaseLength)!,
        DocumentId = Cut(documentId, MaxReferenceLength),
        Title = Cut(title, MaxReferenceLength),
        Position = Cut(position, MaxReferenceLength),
        Url = url is { Length: <= MaxUrlLength } ? url : null,
    };

    private static string? Cut(string? value, int maxLength) =>
        value is { Length: > 0 } ? (value.Length <= maxLength ? value : value[..maxLength]) : null;
}
