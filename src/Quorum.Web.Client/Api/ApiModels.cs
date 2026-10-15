namespace Quorum.Web.Client.Api;

// Client-side copies of the public API contract: the UI depends on the HTTP API only, never on module types.

public sealed record SetupStatus(bool IsSetupMode, string? DefaultSystemPrompt, int? DefaultContextTokenBudget);

public sealed record CurrentUser(string Email, string DisplayName, string Role);

public sealed record ModelConnection(bool Reachable, IReadOnlyList<string> Models);

public sealed record AdminAccount(string Email, string DisplayName, string Password);

public sealed record ModelEndpointSettings(string BaseUrl, string ModelName, string? ApiKey, int ContextTokenBudget);

public sealed record CompleteSetupRequest(AdminAccount Admin, ModelEndpointSettings Model, string SystemPrompt);

public sealed record AssistantSettings(string BaseUrl, string ModelName, bool HasApiKey, int ContextTokenBudget, string SystemPrompt);

/// <summary><c>ApiKey</c>: <see langword="null"/> keeps the stored key, an empty string removes it.</summary>
public sealed record UpdateAssistantSettingsRequest(string BaseUrl, string ModelName, string? ApiKey, int ContextTokenBudget, string SystemPrompt);

public sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset UpdatedAt);

public sealed record ConversationDetail(Guid Id, string? Title, IReadOnlyList<MessageDto> Messages, IReadOnlyList<AttachmentInfo>? Attachments = null);

public sealed record MessageDto(Guid Id, string Role, string Content, string Status, DateTimeOffset CreatedAt);

public static class MessageRoles
{
    public const string User = "User";
    public const string Assistant = "Assistant";
}

public static class MessageStatuses
{
    public const string Completed = "Completed";
    public const string Interrupted = "Interrupted";
    public const string Failed = "Failed";
}

/// <summary>An event of an answer being streamed by the server.</summary>
public abstract record AnswerEvent;

public sealed record AnswerDelta(string Text) : AnswerEvent;

/// <summary>A piece of the model's reasoning: shown while it thinks, not part of the stored answer.</summary>
public sealed record AnswerReasoning(string Text) : AnswerEvent;

public sealed record AnswerDone(Guid MessageId, string Status, string? Error) : AnswerEvent;

/// <summary>The server refused to start the answer (validation, conflict, expired session...).</summary>
public sealed record AnswerRejected(ApiProblem Problem) : AnswerEvent;

/// <summary>A document attached to a conversation (its text is part of the model's context).</summary>
public sealed record AttachmentInfo(Guid Id, string FileName, string MediaType, long SizeBytes, int CharacterCount, DateTimeOffset CreatedAt);

/// <summary>How much of the model's context a conversation uses, in estimated tokens.</summary>
public sealed record ConversationContext(
    int ContextTokenBudget,
    int UsableTokens,
    int SystemPromptTokens,
    int AttachmentsTokens,
    int MessagesTokens,
    int RemainingTokens);
