using System.Text.Json;

namespace Quorum.Web.Client.Api;

// Client-side copies of the public API contract: the UI depends on the HTTP API only, never on module types.

public sealed record SetupStatus(bool IsSetupMode, string? DefaultSystemPrompt, int? DefaultContextTokenBudget);

public sealed record CurrentUser(string Email, string DisplayName, string Role);

/// <param name="SupportsTools">Whether the model accepts tools, when the server reports it; <see langword="null"/> when it does not say.</param>
public sealed record ModelConnection(bool Reachable, IReadOnlyList<string> Models, bool? SupportsTools = null);

public sealed record AdminAccount(string Email, string DisplayName, string Password);

public sealed record ModelEndpointSettings(string BaseUrl, string ModelName, string? ApiKey, int ContextTokenBudget, bool? SupportsTools = null);

public sealed record CompleteSetupRequest(AdminAccount Admin, ModelEndpointSettings Model, string SystemPrompt);

public sealed record AssistantSettings(string BaseUrl, string ModelName, bool HasApiKey, int ContextTokenBudget, string SystemPrompt, bool SupportsTools = true);

/// <summary><c>ApiKey</c>: <see langword="null"/> keeps the stored key, an empty string removes it. <c>SupportsTools</c>: <see langword="null"/> keeps the stored value.</summary>
public sealed record UpdateAssistantSettingsRequest(string BaseUrl, string ModelName, string? ApiKey, int ContextTokenBudget, string SystemPrompt, bool? SupportsTools = null);

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

/// <summary>The model asked for a tool. <c>Arguments</c> is a JSON object, or the text the model sent when it was not valid JSON.</summary>
/// <param name="Tool">The name the model used, e.g. <c>native__calculator</c>.</param>
/// <param name="ExpiresAt">When a call that needs confirmation counts as denied.</param>
public sealed record ToolCallEvent(Guid CallId, string Tool, JsonElement Arguments, bool RequiresConfirmation, DateTimeOffset? ExpiresAt) : AnswerEvent;

/// <summary>A tool call ended. <c>Status</c>: <c>succeeded</c>, <c>failed</c>, <c>denied</c>, <c>cancelled</c> or <c>timed_out</c>.</summary>
public sealed record ToolResultEvent(Guid CallId, string Status, string Content, int SizeCharacters) : AnswerEvent;

/// <summary>The model asked the user a question; the answer waits for the reply (or <c>ExpiresAt</c>).</summary>
public sealed record AskUserEvent(Guid CallId, string Question, DateTimeOffset ExpiresAt) : AnswerEvent;

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
    int RemainingTokens,
    int ToolsTokens = 0);

public sealed record AssistantSummary(Guid Id, string Name);

/// <summary>A tool an assistant may use, and whether it is activated on it.</summary>
public sealed record PoolTool(Guid ToolId, string Name, bool Active);

public sealed record ToolWarning(string Code, string Message);

/// <summary>The tools in an assistant's pool and what to watch out for in the active set (too many, too large).</summary>
public sealed record AssistantTools(IReadOnlyList<PoolTool> Pool, IReadOnlyList<ToolWarning> Warnings);
