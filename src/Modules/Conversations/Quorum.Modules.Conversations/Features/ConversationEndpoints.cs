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
    }

    private static async Task<Ok<List<ConversationSummary>>> ListAsync(ConversationsDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await db.Conversations
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync(cancellationToken));

    private static async Task<Results<Created<ConversationCreated>, ProblemHttpResult>> CreateAsync(
        ConversationsDbContext db,
        IAssistantCatalog assistants,
        ITenantContext tenant,
        ICurrentUser user,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await assistants.GetDefaultAssistantIdAsync(cancellationToken) is not { } assistantId)
        {
            return ConversationErrors.NoAssistant.ToProblem();
        }

        var conversation = Conversation.Start(tenant.TenantId!.Value, user.UserId!.Value, assistantId, clock.GetUtcNow());
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/conversations/{conversation.Id}", new ConversationCreated(conversation.Id));
    }

    private static async Task<Results<Ok<ConversationResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        ConversationsDbContext db,
        CancellationToken cancellationToken)
    {
        // Messages and attachments are two collections: one joined query would multiply their rows.
        var conversation = await db.Conversations
            .AsNoTracking()
            .AsSplitQuery()
            .Where(c => c.Id == id)
            .Select(c => new ConversationResponse(
                c.Id,
                c.Title,
                c.Messages
                    .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
                    .Select(m => new MessageResponse(m.Id, m.Role.ToString(), m.Content, m.Status.ToString(), m.CreatedAt))
                    .ToList(),
                db.Attachments
                    .Where(a => a.ConversationId == c.Id)
                    .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                    .Select(a => new AttachmentResponse(a.Id, a.FileName, a.MediaType, a.SizeBytes, a.CharacterCount, a.CreatedAt))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);

        return conversation is null ? ConversationErrors.NotFound.ToProblem() : TypedResults.Ok(conversation);
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
            var answer = await chat.StartAnswerAsync(conversation.AssistantId, conversation.Id, history, attachments, lease.Token);
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
            var answer = await chat.StartAnswerAsync(conversation.AssistantId, conversation.Id, history, attachments, lease.Token);
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
}

internal sealed record ConversationSummary(Guid Id, string? Title, DateTimeOffset UpdatedAt);

internal sealed record ConversationCreated(Guid Id);

internal sealed record ConversationResponse(Guid Id, string? Title, List<MessageResponse> Messages, List<AttachmentResponse> Attachments);

internal sealed record MessageResponse(Guid Id, string Role, string Content, string Status, DateTimeOffset CreatedAt);

internal sealed record RenameConversationRequest(string? Title);

internal sealed record SendMessageRequest(string? Content);
