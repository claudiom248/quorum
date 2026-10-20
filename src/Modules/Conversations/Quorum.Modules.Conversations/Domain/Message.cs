namespace Quorum.Modules.Conversations.Domain;

internal enum MessageRole
{
    User,
    Assistant,
}

internal enum MessageStatus
{
    Completed,

    /// <summary>The stream was stopped (by the user, or because the conversation was deleted).</summary>
    Interrupted,

    /// <summary>The model failed or was unreachable; the content is what arrived before the failure.</summary>
    Failed,
}

internal sealed class Message
{
    private readonly List<MessageSource> _sources = [];

    private Message()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public MessageRole Role { get; private set; }

    public string Content { get; private set; } = null!;

    public MessageStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The sources the answer cited. Not loaded unless asked for; they are added with the message and removed with it.</summary>
    public IReadOnlyList<MessageSource> Sources => _sources;

    public void AddSource(int n, string knowledgeBase, string? documentId, string? title, string? position, string? url, DateTimeOffset now) =>
        _sources.Add(MessageSource.Create(this, n, knowledgeBase, documentId, title, position, url, now));

    public static Message FromUser(Conversation conversation, string content, DateTimeOffset now) =>
        Create(conversation, MessageRole.User, content, MessageStatus.Completed, now);

    public static Message FromAssistant(Conversation conversation, string content, MessageStatus status, DateTimeOffset now) =>
        Create(conversation, MessageRole.Assistant, content, status, now);

    private static Message Create(Conversation conversation, MessageRole role, string content, MessageStatus status, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = conversation.TenantId,
        ConversationId = conversation.Id,
        Role = role,
        Content = content,
        Status = status,
        CreatedAt = now,
    };
}
