using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Conversations.Domain;
using Quorum.Modules.Conversations.Extraction;
using Quorum.Modules.Conversations.Persistence;
using Quorum.Modules.Conversations.Streaming;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.Conversations.Features;

/// <summary>Documents attached to a conversation: their text becomes part of the model's context (spec 002).</summary>
internal sealed class AttachmentEndpoints : IEndpointGroup
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxAttachments = 10;

    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(60);

    // Room for the multipart envelope around a file of the maximum size; the file size itself is checked precisely.
    private const long MaxRequestBytes = MaxFileBytes + (1024 * 1024);

    public void Map(IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/api/conversations")
            .WithTags("Attachments")
            .RequireAuthorization();

        // Cookie-authenticated requests are protected by the X-Quorum-Request header middleware, not by form antiforgery tokens.
        conversations.MapPost("/{id:guid}/attachments", UploadAsync)
            .WithName("UploadAttachment")
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
        conversations.MapGet("/{id:guid}/attachments", ListAsync).WithName("ListAttachments");
        conversations.MapGet("/{id:guid}/attachments/{attachmentId:guid}/text", TextAsync).WithName("GetAttachmentText");
        conversations.MapDelete("/{id:guid}/attachments/{attachmentId:guid}", RemoveAsync).WithName("RemoveAttachment");
        conversations.MapGet("/{id:guid}/context", ContextAsync).WithName("GetConversationContext");
    }

    /// <summary>The conversation's attachments as the assistant receives them, oldest first.</summary>
    public static async Task<List<ChatAttachment>> LoadForChatAsync(
        ConversationsDbContext db,
        Guid conversationId,
        CancellationToken cancellationToken) =>
        await db.Attachments
            .AsNoTracking()
            .Where(a => a.ConversationId == conversationId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new ChatAttachment(a.FileName, a.Text))
            .ToListAsync(cancellationToken);

    private static async Task<Results<Created<AttachmentResponse>, ProblemHttpResult>> UploadAsync(
        Guid id,
        IFormFile file,
        HttpContext httpContext,
        ConversationsDbContext db,
        ConversationLocks locks,
        IAssistantChat chat,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await db.Conversations.SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is not { } conversation)
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        if (file.Length > MaxFileBytes)
        {
            return AttachmentErrors.TooLarge.ToProblem();
        }

        // Held for the whole upload: no answer can start meanwhile, and two uploads are checked one after the other.
        using var lease = locks.TryAcquire(id, httpContext.RequestAborted);
        if (lease is null)
        {
            return ConversationErrors.AnswerInProgress.ToProblem();
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        {
            using var buffer = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(buffer, lease.Token);
            content = buffer.ToArray();
        }

        var existing = await db.Attachments
            .AsNoTracking()
            .Where(a => a.ConversationId == id)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new { a.FileName, a.Text, a.ContentHash })
            .ToListAsync(lease.Token);

        if (existing.Count >= MaxAttachments)
        {
            return AttachmentErrors.LimitReached.ToProblem();
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (existing.Any(a => a.ContentHash == hash))
        {
            return AttachmentErrors.Duplicate.ToProblem();
        }

        var fileName = DisplayName(file.FileName);
        var extracted = await DocumentTextExtractor.ExtractAsync(file.FileName, content, ExtractionTimeout, lease.Token);
        if (!extracted.IsSuccess)
        {
            return extracted.Error.ToProblem();
        }

        var all = existing.Select(a => new ChatAttachment(a.FileName, a.Text))
            .Append(new ChatAttachment(fileName, extracted.Value.Text))
            .ToList();
        var usage = await chat.MeasureContextAsync(conversation.AssistantId, [], all, lease.Token);
        if (!usage.IsSuccess)
        {
            return usage.Error.ToProblem();
        }

        if (usage.Value.RemainingCharacters < 0)
        {
            return AttachmentErrors.ExceedsBudget(-usage.Value.RemainingCharacters).ToProblem();
        }

        var attachment = Attachment.Create(conversation, fileName, extracted.Value.MediaType, content.Length, hash, extracted.Value.Text, clock.GetUtcNow());
        db.Attachments.Add(attachment);
        try
        {
            await db.SaveChangesAsync(lease.Token);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return AttachmentErrors.Duplicate.ToProblem();
        }

        return TypedResults.Created(
            $"/api/conversations/{id}/attachments/{attachment.Id}",
            AttachmentResponse.From(attachment));
    }

    private static async Task<Results<Ok<List<AttachmentResponse>>, ProblemHttpResult>> ListAsync(
        Guid id,
        ConversationsDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        return TypedResults.Ok(await db.Attachments
            .AsNoTracking()
            .Where(a => a.ConversationId == id)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new AttachmentResponse(a.Id, a.FileName, a.MediaType, a.SizeBytes, a.CharacterCount, a.CreatedAt))
            .ToListAsync(cancellationToken));
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> TextAsync(
        Guid id,
        Guid attachmentId,
        ConversationsDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        var text = await db.Attachments
            .AsNoTracking()
            .Where(a => a.Id == attachmentId && a.ConversationId == id)
            .Select(a => a.Text)
            .SingleOrDefaultAsync(cancellationToken);

        return text is null
            ? AttachmentErrors.NotFound.ToProblem()
            : TypedResults.Text(text, "text/markdown; charset=utf-8");
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        Guid id,
        Guid attachmentId,
        HttpContext httpContext,
        ConversationsDbContext db,
        ConversationLocks locks,
        CancellationToken cancellationToken)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        using var lease = locks.TryAcquire(id, httpContext.RequestAborted);
        if (lease is null)
        {
            return ConversationErrors.AnswerInProgress.ToProblem();
        }

        var removed = await db.Attachments
            .Where(a => a.Id == attachmentId && a.ConversationId == id)
            .ExecuteDeleteAsync(cancellationToken);

        return removed == 0 ? AttachmentErrors.NotFound.ToProblem() : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ContextResponse>, ProblemHttpResult>> ContextAsync(
        Guid id,
        ConversationsDbContext db,
        IAssistantChat chat,
        CancellationToken cancellationToken)
    {
        if (await ConversationEndpoints.LoadAsync(db, id, cancellationToken) is not { } conversation)
        {
            return ConversationErrors.NotFound.ToProblem();
        }

        var attachments = await LoadForChatAsync(db, id, cancellationToken);
        var usage = await chat.MeasureContextAsync(
            conversation.AssistantId,
            ConversationEndpoints.Turns(conversation).ToList(),
            attachments,
            cancellationToken);

        return usage.IsSuccess
            ? TypedResults.Ok(new ContextResponse(
                usage.Value.ContextTokenBudget,
                usage.Value.UsableTokens,
                usage.Value.SystemPromptTokens,
                usage.Value.AttachmentsTokens,
                usage.Value.MessagesTokens,
                usage.Value.RemainingTokens))
            : usage.Error.ToProblem();
    }

    /// <summary>The file name without any path, on one line of plain characters, at most 255 long.</summary>
    private static string DisplayName(string uploadedName)
    {
        var name = Path.GetFileName(uploadedName.Replace('\\', '/'));
        var cleaned = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            cleaned.Append(char.IsControl(character) ? '_' : character);
        }

        var result = cleaned.ToString().Trim();
        if (result.Length == 0)
        {
            return "attachment";
        }

        return result.Length <= Attachment.MaxFileNameLength ? result : result[^Attachment.MaxFileNameLength..];
    }
}

internal static class AttachmentErrors
{
    public static Error NotFound { get; } = Error.NotFound("attachment.not_found", "The attachment does not exist.");

    public static Error TooLarge { get; } = Error.PayloadTooLarge(
        "attachment.too_large",
        $"The file is larger than {AttachmentEndpoints.MaxFileBytes / (1024 * 1024)} MB.");

    public static Error LimitReached { get; } = Error.Conflict(
        "attachment.limit_reached",
        $"A conversation can have at most {AttachmentEndpoints.MaxAttachments} attachments: remove one first.");

    public static Error Duplicate { get; } = Error.Conflict(
        "attachment.duplicate",
        "A file with the same content is already attached to this conversation.");

    public static Error ExceedsBudget(int missingCharacters) => Error.Validation(
        "attachment.exceeds_budget",
        $"The file does not fit this model's context: {missingCharacters} characters too many. " +
        "Remove other attachments, attach a shorter file, or raise the model's context budget in the settings " +
        "(the model server must offer a context at least as large).");
}

internal sealed record AttachmentResponse(Guid Id, string FileName, string MediaType, long SizeBytes, int CharacterCount, DateTimeOffset CreatedAt)
{
    public static AttachmentResponse From(Attachment attachment) =>
        new(attachment.Id, attachment.FileName, attachment.MediaType, attachment.SizeBytes, attachment.CharacterCount, attachment.CreatedAt);
}

internal sealed record ContextResponse(
    int ContextTokenBudget,
    int UsableTokens,
    int SystemPromptTokens,
    int AttachmentsTokens,
    int MessagesTokens,
    int RemainingTokens);
