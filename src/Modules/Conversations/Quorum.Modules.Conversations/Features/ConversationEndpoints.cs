using System.Text.Json.Serialization;
using System.Net.ServerSentEvents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Conversations.Domain;
using Quorum.Modules.Conversations.Persistence;
using Quorum.Modules.Conversations.Streaming;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Conversations.Features;

internal sealed class ConversationEndpoints : IEndpointGroup
{
    internal const int MaxToolAnswerLength = 4000;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    public void Map(IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/api/conversations")
            .WithTags("Conversations")
            .RequireAuthorization();

        conversations.MapGet("/", ListAsync).WithName("ListConversations");
        conversations.MapPost("/", CreateAsync).WithName("CreateConversation");
        conversations.MapGet("/{id:guid}", GetAsync).WithName("GetConversation");
        conversations.MapPatch("/{id:guid}", RenameAsync).WithName("RenameConversation");
        conversations.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteConversation");
        conversations.MapPost("/{id:guid}/messages", SendMessageAsync).WithName("SendMessage");
        conversations.MapPost("/{id:guid}/regenerate", RegenerateAsync).WithName("RegenerateAnswer");
        conversations.MapPost("/{id:guid}/tool-calls/{callId:guid}/decision", DecideToolCallAsync).WithName("DecideToolCall");
        conversations.MapPost("/{id:guid}/tool-calls/{callId:guid}/answer", AnswerQuestionAsync).WithName("AnswerQuestion");

        // The administrator's debug chat: any assistant, its tools, and an endpoint replaced per conversation (spec 018 D-12, D-13, D-15).
        var admin = app.MapGroup("/api/admin/conversations").WithTags("Conversations").RequireAuthorization(QuorumPolicies.AgentsManage);
        admin.MapPost("/", AdminCreateAsync).WithName("AdminCreateConversation");
        admin.MapGet("/", AdminListAsync).WithName("AdminListConversations");
        admin.MapPatch("/{id:guid}/overrides", AdminChangeOverridesAsync).WithName("AdminChangeOverrides");
    }

    /// <summary>Approves or denies a tool call the answer of this conversation waits to run.</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> DecideToolCallAsync(
        Guid id,
        Guid callId,
        ToolDecisionRequest request,
        ConversationsDbContext db,
        IToolInteractions interactions,
        CancellationToken cancellationToken)
    {
        if (request.Approved is not { } approved)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["approved"] = ["Say whether the call is approved."] });
        }

        // The conversation is looked up through the tenant and user filters: someone else's looks like one that does not exist.
        if (!await db.Conversations.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        var result = interactions.Decide(id, callId, approved);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    /// <summary>Answers a question the model asked the user.</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> AnswerQuestionAsync(
        Guid id,
        Guid callId,
        ToolAnswerRequest request,
        ConversationsDbContext db,
        IToolInteractions interactions,
        CancellationToken cancellationToken)
    {
        var answer = request.Answer?.Trim();
        if (string.IsNullOrEmpty(answer) || answer.Length > MaxToolAnswerLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["answer"] = [$"Enter an answer of at most {MaxToolAnswerLength} characters."],
            });
        }

        if (!await db.Conversations.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        var result = interactions.Answer(id, callId, answer);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    /// <summary>A debug conversation: any assistant of the tenant, exposed or not, with the overrides the administrator chose (spec 018 D-12).</summary>
    private static async Task<Results<Created<ConversationCreated>, ValidationProblem, ProblemHttpResult>> AdminCreateAsync(
        AdminConversationRequest? request,
        ConversationsDbContext db,
        IChatAssistantAccess access,
        ITenantContext tenant,
        ICurrentUser user,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request?.AssistantId is not { } assistantId || assistantId == Guid.Empty)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["assistantId"] = ["Choose the assistant to debug."] });
        }

        var disabled = (request.DisabledTools ?? []).Distinct(StringComparer.Ordinal).ToList();
        var selection = await access.CheckSelectionAsync(assistantId, debug: true, disabled, cancellationToken);
        if (selection.Problem == ChatSelectionProblem.NotFound)
        {
            return ConversationErrors.AssistantNotFound.ToProblem();
        }

        if (selection.Problem == ChatSelectionProblem.InvalidNames)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["disabledTools"] = [$"These tools are not attached to this assistant: {string.Join(", ", selection.Names)}."],
            });
        }

        if (request.ModelEndpointId is { } endpointId && await OverrideProblemAsync(access, assistantId, endpointId, cancellationToken) is { } problem)
        {
            return problem;
        }

        var conversation = Conversation.StartDebug(tenant.TenantId!.Value, user.UserId!.Value, assistantId, clock.GetUtcNow(), request.ModelEndpointId, disabled);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/conversations/{conversation.Id}", new ConversationCreated(conversation.Id));
    }

    private static async Task<Ok<List<ConversationSummary>>> AdminListAsync(ConversationsDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await db.Conversations
            .Where(c => c.Mode == ConversationMode.Debug)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync(cancellationToken));

    /// <summary>Changes the overrides of a debug conversation; the next message uses them (spec 018 D-13, D-14).</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> AdminChangeOverridesAsync(
        Guid id,
        ConversationOverridesRequest request,
        ConversationsDbContext db,
        IChatAssistantAccess access,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // A consumer conversation has no overrides to change: the same answer as for one that does not exist (spec 018 AC-15).
        if (await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.Mode == ConversationMode.Debug, cancellationToken) is not { } conversation)
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        var disabled = (request.DisabledTools ?? []).Distinct(StringComparer.Ordinal).ToList();
        var selection = await access.CheckSelectionAsync(conversation.AssistantId, debug: true, disabled, cancellationToken);
        if (selection.Problem == ChatSelectionProblem.NotFound)
        {
            return ConversationErrors.AssistantNotFound.ToProblem();
        }

        if (selection.Problem == ChatSelectionProblem.InvalidNames)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["disabledTools"] = [$"These tools are not attached to this assistant: {string.Join(", ", selection.Names)}."],
            });
        }

        if (request.ModelEndpointId is { } endpointId && await OverrideProblemAsync(access, conversation.AssistantId, endpointId, cancellationToken) is { } problem)
        {
            return problem;
        }

        conversation.ChangeOverrides(request.ModelEndpointId, disabled, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The refusal of an endpoint override that does not exist or cannot carry the root's tools (spec 018 D-12, D-16), or <see langword="null"/>.</summary>
    private static async Task<ProblemHttpResult?> OverrideProblemAsync(IChatAssistantAccess access, Guid assistantId, Guid endpointId, CancellationToken cancellationToken)
    {
        var check = await access.CheckOverrideAsync(assistantId, endpointId, cancellationToken);
        return check.Problem switch
        {
            ChatOverrideProblem.EndpointNotFound => ConversationErrors.ModelNotFound.ToProblem(),
            ChatOverrideProblem.WithoutTools => Error.Validation("conversation.model_without_tools", "This model does not support tools, and the assistant has tools: choose another model.").ToProblem(),
            _ => null,
        };
    }

    private static async Task<Ok<List<ConversationSummary>>> ListAsync(ConversationsDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await db.Conversations
            .Where(c => c.Mode == ConversationMode.Consumer)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync(cancellationToken));

    /// <summary>
    /// A consumer starts a conversation with an exposed assistant and the optional tools it turns off (spec 018 D-8, D-9, D-11).
    /// The assistant is named by the caller: the default one is only the setup's (D-9).
    /// </summary>
    private static async Task<Results<Created<ConversationCreated>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        HttpRequest httpRequest,
        ConversationsDbContext db,
        IChatAssistantAccess access,
        ITenantContext tenant,
        ICurrentUser user,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // The body is read here, not bound: a request without one must reach the authorization, not the fallback of unknown API routes.
        var request = httpRequest.ContentType is not null ? await httpRequest.ReadFromJsonAsync<ConversationCreateRequest>(cancellationToken) : null;
        if (request?.AssistantId is not { } assistantId || assistantId == Guid.Empty)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["assistantId"] = ["Choose the assistant of the conversation."] });
        }

        var disabled = (request.DisabledTools ?? []).Distinct(StringComparer.Ordinal).ToList();
        var check = await access.CheckSelectionAsync(assistantId, debug: false, disabled, cancellationToken);
        switch (check.Problem)
        {
            case ChatSelectionProblem.NotFound:
                return ConversationErrors.AssistantNotFound.ToProblem();
            case ChatSelectionProblem.SelectionNotAllowed:
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["disabledTools"] = ["This assistant does not let you choose its tools."],
                });
            case ChatSelectionProblem.InvalidNames:
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["disabledTools"] = [$"These tools cannot be turned off: {string.Join(", ", check.Names)}. Only optional tools of this assistant can."],
                });
        }

        var conversation = Conversation.Start(tenant.TenantId!.Value, user.UserId!.Value, assistantId, clock.GetUtcNow(), disabled);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/conversations/{conversation.Id}", new ConversationCreated(conversation.Id));
    }

    /// <summary>The turn options of a conversation: the selection it fixed, and for a debug one the endpoint override (spec 018 D-12, D-14).</summary>
    internal static ChatTurnOptions TurnOptionsOf(Conversation conversation) =>
        new(conversation.DisabledTools, conversation.Mode == ConversationMode.Debug, conversation.OverrideModelEndpointId);

    private static ConversationResponse ToResponse(ConversationRow row) => new(
        row.Id,
        row.Title,
        row.Messages,
        row.Attachments,
        row.AssistantId,
        row.DisabledTools,
        row.Mode == ConversationMode.Debug ? new DebugOverridesResponse(row.OverrideModelEndpointId, row.DisabledTools) : null);

    private static async Task<Results<Ok<ConversationResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        ConversationsDbContext db,
        CancellationToken cancellationToken)
    {
        // Messages, their sources and attachments are separate collections: one joined query would multiply their rows.
        var conversation = await db.Conversations
            .AsNoTracking()
            .AsSplitQuery()
            .Where(c => c.Id == id)
            .Select(c => new ConversationRow(
                c.Id,
                c.Title,
                c.Messages
                    .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
                    .Select(m => new MessageResponse(
                        m.Id,
                        m.Role.ToString(),
                        m.Content,
                        m.Status.ToString(),
                        m.CreatedAt,
                        m.Sources
                            .OrderBy(s => s.N)
                            .Select(s => new SourceResponse(s.N, s.KnowledgeBase, s.DocumentId, s.Title, s.Position, s.Url))
                            .ToList()))
                    .ToList(),
                db.Attachments
                    .Where(a => a.ConversationId == c.Id)
                    .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                    .Select(a => new AttachmentResponse(a.Id, a.FileName, a.MediaType, a.SizeBytes, a.CharacterCount, a.CreatedAt))
                    .ToList(),
                c.AssistantId,
                c.DisabledTools,
                c.Mode,
                c.OverrideModelEndpointId))
            .SingleOrDefaultAsync(cancellationToken);

        return conversation is null ? ConversationErrors.NotFound.ToProblem() : TypedResults.Ok(ToResponse(conversation));
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> RenameAsync(
        Guid id,
        RenameConversationRequest request,
        ConversationsDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > Conversation.MaxTitleLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = [$"Enter a title of at most {Conversation.MaxTitleLength} characters."],
            });
        }

        if (await db.Conversations.SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is not { } conversation)
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        conversation.Rename(title, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Permanent: the conversation and its messages are removed. An answer being generated is stopped first.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        ConversationsDbContext db,
        ConversationLocks locks,
        CancellationToken cancellationToken)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        await locks.StopAsync(id, StopTimeout);
        await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<ServerSentEventsResult<object>, ValidationProblem, ProblemHttpResult>> SendMessageAsync(
        Guid id,
        SendMessageRequest request,
        HttpContext httpContext,
        ConversationsDbContext db,
        ConversationLocks locks,
        IAssistantChat chat,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var content = request.Content?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["content"] = ["The message is empty."] });
        }

        if (await LoadAsync(db, id, cancellationToken) is not { } conversation)
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        var lease = locks.TryAcquire(id, httpContext.RequestAborted);
        if (lease is null)
        {
            return ConversationErrors.AnswerInProgress.ToProblem();
        }

        try
        {
            var history = Turns(conversation).Append(new ChatTurn(ChatTurnRole.User, content)).ToList();
            var attachments = await AttachmentEndpoints.LoadForChatAsync(db, conversation.Id, lease.Token);
            var answer = await chat.StartAnswerAsync(conversation.AssistantId, conversation.Id, history, attachments, lease.Token, TurnOptionsOf(conversation));
            if (!answer.IsSuccess)
            {
                return answer.Error.ToProblem();
            }

            conversation.AddUserMessage(content, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);

            var events = AnswerStream.RelayAsync(db, conversation, answer.Value, lease, clock);
            lease = null; // The stream owns the lease from here on and releases it when the answer is stored.
            return TypedResults.ServerSentEvents(events);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>Generates a new answer to the last user message, replacing the last answer if there is one.</summary>
    private static async Task<Results<ServerSentEventsResult<object>, ProblemHttpResult>> RegenerateAsync(
        Guid id,
        HttpContext httpContext,
        ConversationsDbContext db,
        ConversationLocks locks,
        IAssistantChat chat,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await LoadAsync(db, id, cancellationToken) is not { } conversation)
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        if (conversation.Messages.Count == 0)
        {
            return ConversationErrors.NothingToRegenerate.ToProblem();
        }

        var lease = locks.TryAcquire(id, httpContext.RequestAborted);
        if (lease is null)
        {
            return ConversationErrors.AnswerInProgress.ToProblem();
        }

        try
        {
            var replaced = conversation.LastAnswer;
            var history = Turns(conversation, except: replaced).ToList();
            var attachments = await AttachmentEndpoints.LoadForChatAsync(db, conversation.Id, lease.Token);
            var answer = await chat.StartAnswerAsync(conversation.AssistantId, conversation.Id, history, attachments, lease.Token, TurnOptionsOf(conversation));
            if (!answer.IsSuccess)
            {
                return answer.Error.ToProblem();
            }

            if (replaced is not null)
            {
                conversation.RemoveLastAnswer(clock.GetUtcNow());
                db.Messages.Remove(replaced);
                await db.SaveChangesAsync(cancellationToken);
            }

            var events = AnswerStream.RelayAsync(db, conversation, answer.Value, lease, clock);
            lease = null;
            return TypedResults.ServerSentEvents(events);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    internal static Task<Conversation?> LoadAsync(ConversationsDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    // Failed answers are left out of the model's context: they are broken fragments, not part of the dialogue.
    internal static IEnumerable<ChatTurn> Turns(Conversation conversation, Message? except = null) =>
        conversation.Messages
            .Where(m => m.Status != MessageStatus.Failed && !ReferenceEquals(m, except))
            .Select(m => new ChatTurn(m.Role == MessageRole.User ? ChatTurnRole.User : ChatTurnRole.Assistant, m.Content));
}

internal static class ConversationErrors
{
    public static Error NotFound { get; } = Error.NotFound("conversation.not_found", "The conversation does not exist.");

    public static Error AnswerInProgress { get; } =
        Error.Conflict("conversation.answer_in_progress", "An answer is still being generated in this conversation.");

    public static Error NothingToRegenerate { get; } =
        Error.Conflict("conversation.nothing_to_regenerate", "The conversation has no message to answer.");

    public static Error NoAssistant { get; } = Error.Conflict("conversation.no_assistant", "No assistant is configured yet.");

    public static Error AssistantNotFound { get; } = Error.NotFound("conversation.assistant_not_found", "The assistant does not exist or is not available to you.");

    public static Error ModelNotFound { get; } = Error.NotFound("conversation.model_not_found", "The model does not exist in this tenant.");
}

internal sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset UpdatedAt);

internal sealed record ConversationCreated(Guid Id);

/// <summary>The conversation as the client reads it; <see cref="Debug"/> is present only for a debug conversation (spec 018 D-14).</summary>
internal sealed record ConversationResponse(
    Guid Id,
    string? Title,
    List<MessageResponse> Messages,
    List<AttachmentResponse> Attachments,
    Guid AssistantId,
    string[] DisabledTools,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DebugOverridesResponse? Debug);

internal sealed record DebugOverridesResponse(Guid? ModelEndpointId, string[] DisabledTools);

internal sealed record ConversationCreateRequest(Guid? AssistantId, List<string>? DisabledTools);

internal sealed record AdminConversationRequest(Guid? AssistantId, Guid? ModelEndpointId, List<string>? DisabledTools);

internal sealed record ConversationOverridesRequest(Guid? ModelEndpointId, List<string>? DisabledTools);

/// <summary>The projection of a conversation the read returns, before the debug part is decided.</summary>
internal sealed record ConversationRow(
    Guid Id,
    string? Title,
    List<MessageResponse> Messages,
    List<AttachmentResponse> Attachments,
    Guid AssistantId,
    string[] DisabledTools,
    ConversationMode Mode,
    Guid? OverrideModelEndpointId);

/// <param name="Sources">The sources an assistant message cited, by number; empty for a user message and for an answer that cited none.</param>
internal sealed record MessageResponse(Guid Id, string Role, string Content, string Status, DateTimeOffset CreatedAt, List<SourceResponse> Sources);

internal sealed record SourceResponse(int N, string KnowledgeBase, string? DocumentId, string? Title, string? Position, string? Url);

internal sealed record RenameConversationRequest(string? Title);

internal sealed record SendMessageRequest(string? Content);

internal sealed record ToolDecisionRequest(bool? Approved);

internal sealed record ToolAnswerRequest(string? Answer);
