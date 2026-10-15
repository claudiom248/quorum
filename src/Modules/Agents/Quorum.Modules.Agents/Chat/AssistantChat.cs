using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Secrets;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Chat;

internal sealed class AssistantChat(AgentsDbContext db, ApiKeyProtector apiKeyProtector, OpenAiChatStream chatStream) : IAssistantChat, IAssistantCatalog
{
    private static readonly Error AssistantNotFound = Error.NotFound("assistant.not_found", "The assistant does not exist.");

    public async Task<Guid?> GetDefaultAssistantIdAsync(CancellationToken cancellationToken) =>
        await db.Assistants.OrderBy(a => a.Id).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<Result<IAsyncEnumerable<AnswerChunk>>> StartAnswerAsync(
        Guid assistantId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(attachments);

        var configuration = await LoadConfigurationAsync(assistantId, cancellationToken);
        if (configuration is null)
        {
            return AssistantNotFound;
        }

        var turns = ContextWindow.Select(configuration.SystemPrompt, history, attachments, configuration.ContextTokenBudget);
        if (!turns.IsSuccess)
        {
            return turns.Error;
        }

        var block = AttachmentBlock.Render(attachments, AttachmentBlock.NewDelimiter());
        var systemPrompt = configuration.SystemPrompt + block; // the block carries its own separator

        var answer = chatStream.StreamAsync(
            new Uri(configuration.BaseUrl),
            configuration.ModelName,
            apiKeyProtector.Unprotect(configuration.ProtectedApiKey),
            turns.Value,
            systemPrompt,
            cancellationToken);

        return Result<IAsyncEnumerable<AnswerChunk>>.Success(answer);
    }

    public async Task<Result<ContextUsage>> MeasureContextAsync(
        Guid assistantId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(attachments);

        var configuration = await LoadConfigurationAsync(assistantId, cancellationToken);
        if (configuration is null)
        {
            return AssistantNotFound;
        }

        return ContextWindow.Measure(configuration.SystemPrompt, history, attachments, configuration.ContextTokenBudget);
    }

    public async Task<Result<ContextUsage>> MeasureContextBySizesAsync(
        Guid assistantId,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ChatAttachmentSize> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(attachments);

        var configuration = await LoadConfigurationAsync(assistantId, cancellationToken);
        if (configuration is null)
        {
            return AssistantNotFound;
        }

        return ContextWindow.Measure(configuration.SystemPrompt, history, attachments, configuration.ContextTokenBudget);
    }

    private Task<AssistantConfiguration?> LoadConfigurationAsync(Guid assistantId, CancellationToken cancellationToken) =>
        (from assistant in db.Assistants
         join endpoint in db.ModelEndpoints on assistant.ModelEndpointId equals endpoint.Id
         where assistant.Id == assistantId
         select new AssistantConfiguration(assistant.SystemPrompt, endpoint.BaseUrl, endpoint.ModelName, endpoint.ProtectedApiKey, endpoint.ContextTokenBudget))
        .AsNoTracking()
        .SingleOrDefaultAsync(cancellationToken);

    private sealed record AssistantConfiguration(string SystemPrompt, string BaseUrl, string ModelName, string? ProtectedApiKey, int ContextTokenBudget);
}
