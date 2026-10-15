using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Secrets;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Chat;

internal sealed class AssistantChat(AgentsDbContext db, ApiKeyProtector apiKeyProtector, ChatClientFactory chatClientFactory) : IAssistantChat, IAssistantCatalog
{
    private static readonly Error AssistantNotFound = Error.NotFound("assistant.not_found", "The assistant does not exist.");

    public async Task<Guid?> GetDefaultAssistantIdAsync(CancellationToken cancellationToken) =>
        await db.Assistants.OrderBy(a => a.Id).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<Result<IAsyncEnumerable<string>>> StartAnswerAsync(
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

        List<ChatMessage> messages =
        [
            new(ChatRole.System, configuration.SystemPrompt),
            .. turns.Value.Select(turn => new ChatMessage(turn.Role == ChatTurnRole.User ? ChatRole.User : ChatRole.Assistant, turn.Content)),
        ];

        var client = chatClientFactory.Create(
            new Uri(configuration.BaseUrl),
            configuration.ModelName,
            apiKeyProtector.Unprotect(configuration.ProtectedApiKey));

        return Result<IAsyncEnumerable<string>>.Success(StreamAsync(client, messages, configuration.BaseUrl, cancellationToken));
    }

    private static async IAsyncEnumerable<string> StreamAsync(
        IChatClient client,
        List<ChatMessage> messages,
        string baseUrl,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var _ = client;
        await using var updates = client.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            string text;
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    yield break;
                }

                text = updates.Current.Text;
            }
            catch (HttpRequestException exception)
            {
                throw new ModelStreamException($"Cannot reach the model at {baseUrl}: {exception.Message}", exception);
            }
            catch (IOException exception)
            {
                throw new ModelStreamException($"The connection to the model broke: {exception.Message}", exception);
            }
            catch (ClientResultException exception) when (exception.Status == 0)
            {
                // No HTTP response at all: the OpenAI client wraps connection failures this way.
                throw new ModelStreamException($"Cannot reach the model at {baseUrl}: {exception.Message}", exception);
            }
            catch (ClientResultException exception)
            {
                throw new ModelStreamException($"The model answered with an error (HTTP {exception.Status}): {exception.Message}", exception);
            }
            catch (JsonException exception)
            {
                throw new ModelStreamException("The model sent a response that is not OpenAI-compatible.", exception);
            }

            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }
}
