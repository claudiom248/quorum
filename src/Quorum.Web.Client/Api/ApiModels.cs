using System.Text.Json;

namespace Quorum.Web.Client.Api;

// Client-side copies of the public API contract: the UI depends on the HTTP API only, never on module types.

public sealed record SetupStatus(bool IsSetupMode, string? DefaultSystemPrompt, int? DefaultContextTokenBudget);

public sealed record CurrentUser(string Email, string DisplayName, string Role);

/// <param name="LedgerStyle"><c>classic</c>, <c>cards</c>, <c>timeline</c> or <c>bars</c>: how the tool calls of an answer are drawn.</param>
public sealed record UserPreferences(string LedgerStyle);

/// <param name="SupportsTools">Whether the model accepts tools, when the server reports it; <see langword="null"/> when it does not say.</param>
public sealed record ModelConnection(bool Reachable, IReadOnlyList<string> Models, bool? SupportsTools = null);

public sealed record AdminAccount(string Email, string DisplayName, string Password);

public sealed record ModelEndpointSettings(string BaseUrl, string ModelName, string? ApiKey, int ContextTokenBudget, bool? SupportsTools = null);

public sealed record CompleteSetupRequest(AdminAccount Admin, ModelEndpointSettings Model, string? SystemPrompt = null);

public sealed record AssistantSettings(string BaseUrl, string ModelName, bool HasApiKey, int ContextTokenBudget, string SystemPrompt, bool SupportsTools = true);

/// <summary><c>ApiKey</c>: <see langword="null"/> keeps the stored key, an empty string removes it. <c>SupportsTools</c>: <see langword="null"/> keeps the stored value.</summary>
public sealed record UpdateAssistantSettingsRequest(string BaseUrl, string ModelName, string? ApiKey, int ContextTokenBudget, string SystemPrompt, bool? SupportsTools = null);

public sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset UpdatedAt);

/// <param name="Debug">The overrides of a debug conversation (spec 018 D-14); <see langword="null"/> for a consumer conversation.</param>
public sealed record ConversationDetail(Guid Id, string? Title, IReadOnlyList<MessageDto> Messages, IReadOnlyList<AttachmentInfo>? Attachments = null, Guid? AssistantId = null, IReadOnlyList<string>? DisabledTools = null, DebugOverridesInfo? Debug = null);

/// <summary>The overrides of a debug conversation (spec 018 D-12).</summary>
public sealed record DebugOverridesInfo(Guid? ModelEndpointId, IReadOnlyList<string> DisabledTools);

/// <param name="Sources">What an assistant message cited, by number; none for a user message or an answer that cited nothing.</param>
public sealed record MessageDto(Guid Id, string Role, string Content, string Status, DateTimeOffset CreatedAt, IReadOnlyList<MessageSourceDto>? Sources = null);

/// <summary>A reference to where a retrieved passage came from. Never the passage itself.</summary>
public sealed record MessageSourceDto(int N, string KnowledgeBase, string? DocumentId, string? Title, string? Position, string? Url);

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
/// <param name="Tool">The name the model used: the tool's own name (<c>calculator</c>), or <c>server__tool</c> for an MCP tool.</param>
/// <param name="ExpiresAt">When a call that needs confirmation counts as denied.</param>
/// <param name="ParentCallId">The delegation call a sub-agent made this call under; <see langword="null"/> for the root agent's calls.</param>
/// <param name="AgentSlug">The slug of the sub-agent that made the call; <see langword="null"/> for the root agent's calls.</param>
/// <param name="IsDelegation">The call is a delegation to a sub-agent, named by its slug: the name alone no longer says it.</param>
public sealed record ToolCallEvent(Guid CallId, string Tool, JsonElement Arguments, bool RequiresConfirmation, DateTimeOffset? ExpiresAt, Guid? ParentCallId = null, string? AgentSlug = null, bool IsDelegation = false) : AnswerEvent;

/// <summary>A tool call ended. <c>Status</c>: <c>succeeded</c>, <c>failed</c>, <c>denied</c>, <c>cancelled</c> or <c>timed_out</c>.</summary>
public sealed record ToolResultEvent(Guid CallId, string Status, string Content, int SizeCharacters) : AnswerEvent;

/// <summary>A knowledge base search returned these sources; numbering continues across the calls of one answer.</summary>
public sealed record SourcesEvent(IReadOnlyList<MessageSourceDto> Sources) : AnswerEvent;

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

public sealed record AssistantSummary(Guid Id, string Name, string? RootAgentSlug = null);

/// <param name="Current">Whether this is the tenant of the signed-in user: the one a scope never lists as "another tenant".</param>
public sealed record ChecklistItem(string Key, bool Done);

public sealed record Checklist(List<ChecklistItem> Items, bool Dismissed)
{
    /// <summary>The checklist shows until every step is done or an admin dismisses it.</summary>
    public bool IsVisible => !Dismissed && !Items.All(i => i.Done);
}

public sealed record TenantSummary(Guid Id, string Name, bool Current = false);

/// <summary>A service token as the admin sees it in the list: the value is only in <see cref="CreatedServiceToken"/>.</summary>
public sealed record ServiceTokenInfo(Guid Id, DateTimeOffset CreatedAt);

/// <param name="Token">The plaintext, shown once at generation and never returned again.</param>
public sealed record CreatedServiceToken(Guid Id, string Token, DateTimeOffset CreatedAt);

/// <summary>A tool an agent may use, and whether it is activated on it.</summary>
/// <param name="Manifest">For a lookup tool: <c>Full</c> or <c>Lean</c> on this agent; <see langword="null"/> for the other tools.</param>
/// <param name="Name">What the model calls the tool: its own name, or <c>server__tool</c> for an MCP tool.</param>
/// <param name="Origin">Where it comes from: <c>builtin</c>, <c>rag</c>, <c>lookup</c>, <c>web_search</c>, <c>web_fetch</c> or <c>mcp</c>.</param>
/// <param name="Server">The MCP server of an MCP tool.</param>
/// <param name="Mode">Required or Optional on this agent (spec 018 D-3); the picker shows it for the active tools.</param>
public sealed record PoolTool(Guid ToolId, string Name, bool Active, string? Manifest = null, string Origin = "mcp", string? Server = null, string Mode = "Required");

/// <summary>A root tool of an assistant as the chat shows it (spec 018 D-6): its name as the model sees it, its description and its mode.</summary>
public sealed record ChatTool(string Name, string Description, string Mode);

public sealed record ToolWarning(string Code, string Message);

/// <summary>The tools in an agent's pool and what to watch out for in the active set (too many, too large).</summary>
public sealed record AgentTools(IReadOnlyList<PoolTool> Pool, IReadOnlyList<ToolWarning> Warnings);

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

    /// <summary>Tools and servers are scoped to agents (spec 013 D-17); knowledge bases and lookups keep <see cref="Assistant"/>.</summary>
    public const string Agent = "Agent";
}

/// <param name="Annotations">What the server claims about the tool (<c>readOnlyHint</c>...): information only, it never changes the policy.</param>
/// <param name="Name">What the model and people read: for a built-in tool, the name the administrator gave it or else its default.</param>
/// <param name="DefaultName">The name a built-in tool comes with, which it can be given back; <see langword="null"/> for the other tools.</param>
public sealed record ToolInfo(
    Guid Id,
    string Origin,
    string? ServerName,
    string Name,
    string Description,
    string State,
    string Policy,
    JsonElement? Annotations,
    Guid? ServerId = null,
    ToolScope? ServerScope = null,
    bool Shared = false,
    string? DefaultName = null);

/// <param name="TenantIds">Tenants besides the owner that see the server (scope <c>Tenant</c>).</param>
/// <param name="AssistantIds">Assistants that may use a knowledge base or lookup (scope <c>Assistant</c>); empty for tools and servers.</param>
/// <param name="AgentIds">Agents that may use a tool or server (scope <c>Agent</c>); empty for knowledge bases and lookups.</param>
public sealed record ToolScope(string Kind, IReadOnlyList<Guid> TenantIds, IReadOnlyList<Guid>? AssistantIds = null, IReadOnlyList<Guid>? AgentIds = null)
{
    public IReadOnlyList<Guid> AssistantIds { get; init; } = AssistantIds ?? [];

    public IReadOnlyList<Guid> AgentIds { get; init; } = AgentIds ?? [];
}

/// <summary>An agent as the scope pickers list it.</summary>
public sealed record AgentSummary(Guid Id, string Slug, string DisplayName);

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

/// <summary>One page of a list. <paramref name="Cursor"/> is set when there is a next one.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);

/// <summary>One recorded tool call. The audit log is read-only.</summary>
public sealed record ToolAuditItem(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    Guid UserId,
    Guid AssistantId,
    Guid ConversationId,
    Guid CallId,
    string? ServerName,
    string ToolName,
    string? Decision,
    string Outcome,
    string Arguments,
    bool ArgumentsTruncated,
    int? ResultCharacters,
    int? SourceCount,
    int? DurationMs,
    string? ErrorCode,
    string? TraceId,
    string? TraceUrl);

/// <param name="Total">The entries that match the filters, across all pages.</param>
public sealed record ToolAuditPage(IReadOnlyList<ToolAuditItem> Items, int Total);

public static class AuditOutcomes
{
    public static readonly IReadOnlyList<string> All = ["Started", "Succeeded", "Failed", "Denied", "Cancelled", "TimedOut"];
}
