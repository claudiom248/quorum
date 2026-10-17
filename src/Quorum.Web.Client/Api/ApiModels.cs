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
/// <param name="Tool">The name the model used, e.g. <c>builtin__calculator</c>.</param>
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

public static class ToolStates
{
    public const string PendingReview = "PendingReview";
    public const string Enabled = "Enabled";
    public const string Disabled = "Disabled";
}

public static class ToolPolicies
{
    public const string Auto = "Auto";
    public const string Confirm = "Confirm";
}

public static class ServerStatuses
{
    public const string Available = "Available";
    public const string Unavailable = "Unavailable";
}

public static class ScopeKinds
{
    public const string Global = "Global";
    public const string Tenant = "Tenant";
    public const string Assistant = "Assistant";
}

/// <param name="Annotations">What the server claims about the tool (<c>readOnlyHint</c>...): information only, it never changes the policy.</param>
public sealed record ToolInfo(
    Guid Id,
    string Origin,
    string? ServerName,
    string Name,
    string Description,
    string State,
    string Policy,
    JsonElement? Annotations);

/// <param name="TenantIds">Tenants besides the owner that see the server (scope <c>Tenant</c>).</param>
/// <param name="AssistantIds">Assistants that may use the server (scope <c>Assistant</c>).</param>
public sealed record ToolScope(string Kind, IReadOnlyList<Guid> TenantIds, IReadOnlyList<Guid> AssistantIds);

/// <param name="HasHeaders">Whether static headers are stored. Their values are never returned.</param>
/// <param name="Shared"><see langword="true"/> for a server of another tenant made visible by its scope: read-only here.</param>
/// <param name="Tools">Only in the detail of one server.</param>
public sealed record ToolServer(
    Guid Id,
    string Name,
    string Url,
    bool HasHeaders,
    string Status,
    string? LastError,
    DateTimeOffset? LastRefreshedAt,
    ToolScope Scope,
    int ToolCount,
    int PendingReviewCount,
    bool Shared,
    IReadOnlyList<ToolInfo>? Tools = null);

/// <param name="Headers">Omitted on an update: the stored ones stay. An empty object removes them.</param>
public sealed record ToolServerRequest(string Name, string Url, IReadOnlyDictionary<string, string>? Headers, ToolScope Scope);

public sealed record TestedTool(string Name, string Description, JsonElement? Annotations);

public sealed record ToolServerTest(bool Reachable, IReadOnlyList<TestedTool> Tools);

public sealed record CreatedToolServer(Guid Id, string Status, string? Warning, IReadOnlyList<ToolInfo> Tools);

public sealed record ToolRefresh(string Status, IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed);
