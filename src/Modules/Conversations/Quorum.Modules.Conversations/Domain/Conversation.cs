using System.Text.RegularExpressions;

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

    /// <summary>A consumer conversation (fixed selection) or a debug one that the administrator may reconfigure (spec 018 D-12, D-13).</summary>
    public ConversationMode Mode { get; private set; } = ConversationMode.Consumer;

    /// <summary>Tool names as the model sees them, turned off for the turns of this conversation (spec 018 D-7, D-8).</summary>
    public string[] DisabledTools { get; private set; } = [];

    /// <summary>The endpoint of a debug conversation, replacing the root agent's (spec 018 D-12); <see langword="null"/> otherwise.</summary>
    public Guid? OverrideModelEndpointId { get; private set; }

    public static Conversation Start(Guid tenantId, Guid userId, Guid assistantId, DateTimeOffset now, IReadOnlyList<string>? disabledTools = null) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        UserId = userId,
        AssistantId = assistantId,
        CreatedAt = now,
        UpdatedAt = now,
        DisabledTools = [.. disabledTools ?? []],
    };

    public static Conversation StartDebug(Guid tenantId, Guid userId, Guid assistantId, DateTimeOffset now, Guid? modelEndpointId, IReadOnlyList<string> disabledTools)
    {
        var conversation = Start(tenantId, userId, assistantId, now, disabledTools);
        conversation.Mode = ConversationMode.Debug;
        conversation.OverrideModelEndpointId = modelEndpointId;
        return conversation;
    }

    /// <summary>Changes the overrides of a debug conversation; the next message uses them (spec 018 D-13, D-14).</summary>
    public void ChangeOverrides(Guid? modelEndpointId, IReadOnlyList<string> disabledTools, DateTimeOffset now)
    {
        OverrideModelEndpointId = modelEndpointId;
        DisabledTools = [.. disabledTools];
        UpdatedAt = now;
    }

    public Message AddUserMessage(string content, DateTimeOffset now)
    {
        if (_messages.Count == 0 && Title is null)
        {
            Title = TitleFrom(content);
        }

        return Add(Message.FromUser(this, content, now), now);
    }

    public Message AddAnswer(string content, MessageStatus status, DateTimeOffset now) =>
        Add(Message.FromAssistant(this, content, status, now), now);

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
