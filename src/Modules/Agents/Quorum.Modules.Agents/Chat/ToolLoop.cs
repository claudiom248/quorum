using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Modules.Agents.Chat;

/// <summary>Everything one answer needs, fixed when it starts: the model, the request so far, the tools and the limits.</summary>
/// <param name="ToolsJson">The rendered definitions of the offered tools, or <see langword="null"/> when none are offered.</param>
/// <param name="BaseCharacters">What the request takes before the loop adds anything: system prompt, attachments, definitions and history.</param>
/// <param name="UsableCharacters">The model's whole context budget, in characters.</param>
internal sealed record ToolLoopRequest(
    Uri BaseUrl,
    string ModelName,
    string? ApiKey,
    IReadOnlyList<ModelMessage> Messages,
    string? ToolsJson,
    int BaseCharacters,
    int UsableCharacters,
    ToolRunContext Context);

/// <summary>
/// The answer of an assistant: calls the model, runs the tools it asks for one at a time in the order it asked, sends the results
/// back, and repeats until the model answers in text. Rounds, total time and context are bounded; reaching a bound fails the
/// answer with a readable error and keeps the text already streamed.
/// </summary>
internal sealed class ToolLoop(OpenAiChatStream stream, ToolExecutor executor, TimeProvider clock)
{
    public async IAsyncEnumerable<AnswerChunk> RunAsync(ToolLoopRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var limits = request.Context.Limits;
        using var timeLimit = new CancellationTokenSource(limits.TotalTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeLimit.Token);

        await using var rounds = RunRoundsAsync(request, cancellationToken, linked.Token).GetAsyncEnumerator(linked.Token);
        while (true)
        {
            bool moved;
            try
            {
                moved = await rounds.MoveNextAsync();
            }
            catch (OperationCanceledException) when (timeLimit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ModelStreamException(
                    $"The answer reached the total time limit of {Format(limits.TotalTimeout)} seconds for a tool-calling answer and was stopped. " +
                    "An administrator can raise it in the tool settings.");
            }

            if (!moved)
            {
                yield break;
            }

            yield return rounds.Current;
        }
    }

    private static string Format(TimeSpan span) => span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private async IAsyncEnumerable<AnswerChunk> RunRoundsAsync(
        ToolLoopRequest request,
        CancellationToken userCancellation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var limits = request.Context.Limits;
        var messages = new List<ModelMessage>(request.Messages);
        var characters = request.BaseCharacters;

        for (var round = 1; round <= limits.MaxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = new StringBuilder();
            IReadOnlyList<ModelToolCall> calls = [];
            await foreach (var part in stream.StreamRoundAsync(request.BaseUrl, request.ModelName, request.ApiKey, messages, request.ToolsJson, cancellationToken))
            {
                if (part.Chunk is { } chunk)
                {
                    if (chunk.Kind == AnswerChunkKind.Text)
                    {
                        text.Append(chunk.Text);
                    }

                    yield return chunk;
                }
                else if (part.ToolCalls is { } asked)
                {
                    calls = asked;
                }
            }

            if (calls.Count == 0)
            {
                yield break;
            }

            // The calls of the last round could never be answered, so they are not run.
            if (round == limits.MaxRounds)
            {
                throw new ModelStreamException(
                    $"The model kept asking for tools after {limits.MaxRounds} rounds, so the answer was stopped. " +
                    "An administrator can raise the limit in the tool settings.");
            }

            var asking = new AssistantMessage(text.ToString(), calls.Select(Normalized).ToList());
            messages.Add(asking);
            characters += asking.Length;

            foreach (var call in calls)
            {
                var callId = Guid.CreateVersion7();
                yield return new AnswerChunk(AnswerChunkKind.ToolCall, string.Empty, new ToolCallEvent(callId, call.Name, call.Arguments, false, null));

                var result = await executor.ExecuteAsync(call, callId, request.Context, cancellationToken, userCancellation);

                yield return new AnswerChunk(AnswerChunkKind.ToolResult, string.Empty, new ToolResultEvent(callId, result.Status, result.Content, result.Content.Length));

                var answer = new ToolMessage(call.Id, result.Content);
                messages.Add(answer);
                characters += answer.Length;

                if (characters > request.UsableCharacters)
                {
                    throw new ModelStreamException(
                        "The tool results filled the context of the model, so the answer was stopped. " +
                        "Use tools that return less, or raise the context budget in the settings.");
                }
            }
        }
    }

    // The arguments go back to the model as it sent them, except that text that is not an object becomes {} (servers reject the rest).
    private static ModelToolCall Normalized(ModelToolCall call)
    {
        try
        {
            using var document = JsonDocument.Parse(call.Arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object ? call : call with { Arguments = "{}" };
        }
        catch (JsonException)
        {
            return call with { Arguments = "{}" };
        }
    }
}
