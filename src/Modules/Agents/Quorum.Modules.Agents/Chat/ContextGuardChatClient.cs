using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// Sits under the function-invoking client, so it sees every request to the model. Before a request that carries tool results it
/// checks that the whole request still fits the model's context budget, and each request runs under a token that the total time
/// limit can cancel (and that does not run while the answer waits for a person).
/// </summary>
internal sealed class ContextGuardChatClient(IChatClient inner, AnswerRun run) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var history = messages as IList<ChatMessage> ?? messages.ToList();

        // The first request was measured before the answer started; only the results of tools can make it outgrow the budget.
        if (history.Any(message => message.Contents.OfType<FunctionResultContent>().Any()) && Characters(history, options) > run.UsableCharacters)
        {
            throw new ModelStreamException(
                "The tool results filled the context of the model, so the answer was stopped. " +
                "Use tools that return less, or raise the context budget in the settings.");
        }

        using var watch = run.Deadline.CreateWatch(cancellationToken);
        await using var updates = base.GetStreamingResponseAsync(history, options, watch.Token).GetAsyncEnumerator(watch.Token);
        while (true)
        {
            bool moved;
            try
            {
                moved = await updates.MoveNextAsync();
            }
            catch (OperationCanceledException) when (watch.TimedOut && !cancellationToken.IsCancellationRequested)
            {
                run.StopFor(AnswerStop.TotalTime);
                throw;
            }

            if (!moved)
            {
                yield break;
            }

            yield return updates.Current;
        }
    }

    // The same tool list goes to every request of the answer (the function-invoking client swaps it for another only on its last one),
    // so what its definitions cost is worked out once per list.
    private IList<AITool>? _countedTools;
    private int _countedToolsCharacters;

    private int ToolsCharacters(IList<AITool>? tools)
    {
        if (tools is null)
        {
            return 0;
        }

        if (!ReferenceEquals(tools, _countedTools))
        {
            _countedToolsCharacters = ToolDefinitionRenderer.Length(tools.OfType<AIFunctionDeclaration>().Select(ToolDefinition.From).ToList());
            _countedTools = tools;
        }

        return _countedToolsCharacters;
    }

    /// <summary>What the request takes in the context, in characters: the instructions, the tool definitions and the messages.</summary>
    private int Characters(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var characters = options?.Instructions?.Length ?? 0;

        characters += ToolsCharacters(options?.Tools);

        foreach (var content in messages.SelectMany(message => message.Contents))
        {
            characters += content switch
            {
                TextContent text => text.Text.Length,
                FunctionCallContent call => call.CallId.Length + call.Name.Length + OpenAiChatClient.ArgumentsJson(call).Length,
                FunctionResultContent result => OpenAiChatClient.ResultText(result).Length,
                _ => 0,
            };
        }

        return characters;
    }
}
