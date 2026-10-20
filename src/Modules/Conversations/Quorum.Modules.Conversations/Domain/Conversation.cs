using System.Text.RegularExpressions;
using Quorum.Modules.Agents.Contracts;

namespace Quorum.Modules.Conversations.Domain;

internal sealed partial class Conversation
{
    public const int MaxTitleLength = 60;

    private readonly List<Message> _messages = [];

    private Conversation()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid AssistantId { get; private set; }

    /// <summary><see langword="null"/> until the first message.</summary>
    public string? Title { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<Message> Messages => _messages;

    public static Conversation Start(Guid tenantId, Guid userId, Guid assistantId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        UserId = userId,
        AssistantId = assistantId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public Message AddUserMessage(string content, DateTimeOffset now)
    {
        if (_messages.Count == 0 && Title is null)
        {
            Title = TitleFrom(content);
        }

        return Add(Message.FromUser(this, content, now), now);
    }

    public Message AddAnswer(string content, MessageStatus status, IEnumerable<AnswerSource> sources, DateTimeOffset now)
    {
        var message = Message.FromAssistant(this, content, status, now);
        foreach (var source in sources)
        {
            message.AddSource(source.N, source.KnowledgeBase, source.DocumentId, source.Title, source.Position, source.Url, now);
        }

        return Add(message, now);
    }

    /// <summary>The answer regeneration replaces, if the conversation ends with one.</summary>
    public Message? LastAnswer => _messages.Count > 0 && _messages[^1].Role == MessageRole.Assistant ? _messages[^1] : null;

    public void RemoveLastAnswer(DateTimeOffset now)
    {
        if (LastAnswer is { } answer)
        {
            _messages.Remove(answer);
            UpdatedAt = now;
        }
    }

    public void Rename(string title, DateTimeOffset now)
    {
        Title = title;
        UpdatedAt = now;
    }

    /// <summary>The first words of the message, at most <see cref="MaxTitleLength"/> characters, cut at a word boundary.</summary>
    public static string TitleFrom(string content)
    {
        var text = Whitespace().Replace(content, " ").Trim();
        if (text.Length <= MaxTitleLength)
        {
            return text;
        }

        if (text[MaxTitleLength] == ' ')
        {
            return text[..MaxTitleLength];
        }

        var lastSpace = text.LastIndexOf(' ', MaxTitleLength - 1);
        return lastSpace > 0 ? text[..lastSpace] : text[..MaxTitleLength];
    }

    private Message Add(Message message, DateTimeOffset now)
    {
        _messages.Add(message);
        UpdatedAt = now;
        return message;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
