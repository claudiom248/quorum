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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);

        var configuration = await (
            from assistant in db.Assistants
            join endpoint in db.ModelEndpoints on assistant.ModelEndpointId equals endpoint.Id
            where assistant.Id == assistantId
            select new { assistant.SystemPrompt, endpoint.BaseUrl, endpoint.ModelName, endpoint.ProtectedApiKey, endpoint.ContextTokenBudget })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        if (configuration is null)
        {
            return AssistantNotFound;
        }

        var turns = ContextWindow.Select(configuration.SystemPrompt, history, configuration.ContextTokenBudget);
        if (!turns.IsSuccess)
        {
            return turns.Error;
        }

        var answer = chatStream.StreamAsync(
            new Uri(configuration.BaseUrl),
            configuration.ModelName,
            apiKeyProtector.Unprotect(configuration.ProtectedApiKey),
            turns.Value,
            configuration.SystemPrompt,
            cancellationToken);

        return Result<IAsyncEnumerable<AnswerChunk>>.Success(answer);
    }
}
