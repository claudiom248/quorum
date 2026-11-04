using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Telemetry;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Mcp;
using Quorum.Shared.Telemetry;

namespace Quorum.Modules.Agents.Chat;

/// <summary>Everything one answer needs, fixed when it starts: the model, the request so far, the tools and the limits.</summary>
/// <param name="Instructions">The system prompt, with the notice about tool results when tools are offered.</param>
/// <param name="AttachmentsBlock">The rendered attachments, or <see langword="null"/> when the conversation has none.</param>
/// <param name="Tools">The tools offered to the model; empty when the answer has none.</param>
/// <param name="UsableCharacters">The model's whole context budget, in characters.</param>
internal sealed record AnswerRequest(
    Uri BaseUrl,
    string ModelName,
    string? ApiKey,
    string Instructions,
    string? AttachmentsBlock,
    IReadOnlyList<ChatMessage> History,
    IReadOnlyList<QuorumTool> Tools,
    int UsableCharacters,
    ToolRunContext Context,
    McpAnswerConnections? Connections = null,
    int MaxParallelSubAgents = 1);

/// <summary>
/// The answer of an assistant: a <see cref="ChatClientAgent"/> over our model adapter and a function-invoking client whose
/// invoker is <see cref="ToolCallInvoker"/>. The agent runs on a background task and writes everything the user can watch (text,
/// reasoning, tool calls and results) to one channel, so a tool call reaches the client even while the invoker waits for something.
/// Rounds, total time and context are bounded; reaching a bound fails the answer with a readable error and keeps the text already streamed.
/// The answer is also the root span of its trace (spec 016 D-7, D-13); the frameworks' spans of the model and the agent are its children.
/// </summary>
internal sealed partial class AnswerPipeline(
    IHttpClientFactory httpClientFactory,
    AgentsDatabaseGate databaseGate,
    ToolCallInvoker invoker,
    PendingInteractions interactions,
    TimeProvider clock,
    IOptions<TelemetryOptions> telemetry,
    ILogger<AnswerPipeline> logger)
{
    public async IAsyncEnumerable<AnswerChunk> RunAsync(AnswerRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channel = Channel.CreateBounded<AnswerChunk>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var run = new AnswerRun(request.Context, new AnswerDeadline(request.Context.Limits.TotalTimeout, clock), channel.Writer, request.UsableCharacters, request.MaxParallelSubAgents, databaseGate.Semaphore);

        // Started before the producer, so the producer's task and every span under it run under the turn.
        using var turn = AgentsTelemetry.Source.StartActivity(AgentsTelemetry.TurnActivityName, ActivityKind.Internal);
        if (turn is not null)
        {
            AgentsTelemetry.TagTurn(turn, request.Context);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(() => ProduceAsync(request, run, channel.Writer, turn, cancellationToken, stop.Token), CancellationToken.None);
        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return chunk;
            }
        }
        finally
        {
            // The producer is done before the answer is, so that what it records (a cancelled tool call) is recorded when the caller goes on.
            await stop.CancelAsync();
            await producer;

            // The sessions with MCP servers were opened for this answer only.
            if (request.Connections is not null)
            {
                await request.Connections.DisposeAsync();
            }

            // No one waits for the user any more; a reply that comes now is for an answer that is over.
            interactions.CloseConversation(request.Context.ConversationId);
        }
    }

    private async Task ProduceAsync(
        AnswerRequest request,
        AnswerRun run,
        ChannelWriter<AnswerChunk> writer,
        Activity? turn,
        CancellationToken userCancellation,
        CancellationToken cancellationToken)
    {
        var limits = request.Context.Limits;
        var captureContent = telemetry.Value.CaptureContent;

        // The logs of the answer carry its identifiers too (spec 016 D-9), as do its spans.
        using var scope = logger.BeginScope(AgentsTelemetry.Identifiers(request.Context));

        // Why the answer ended, as the root span says it (spec 016 D-13); set before the answer is ended by one of its limits.
        string? stop = null;
        try
        {
            using var model = new OpenAiChatClient(httpClientFactory, request.BaseUrl, request.ModelName, request.ApiKey, offersTools: request.Tools.Count > 0);

            // The model's span is the framework's: Quorum only decides whether its content is recorded (spec 016 D-2).
            using var traced = new OpenTelemetryChatClient(new ContextGuardChatClient(model, run)) { EnableSensitiveData = captureContent };
            using var tools = new FunctionInvokingChatClient(traced)
            {
                // The calls of a round start together so that delegations can run in parallel; the invoker runs ordinary tools one at a time.
                AllowConcurrentInvocation = true,

                // The client makes one more request, without tools, after the last iteration it allows; the limit counts that request.
                MaximumIterationsPerRequest = Math.Max(1, limits.MaxRounds - 1),
                FunctionInvoker = (invocation, token) => invoker.InvokeAsync(run, invocation, token),
            };

            var agent = new ChatClientAgent(
                tools,
                new ChatClientAgentOptions
                {
                    UseProvidedChatClientAsIs = true,
                    ChatOptions = new ChatOptions
                    {
                        Instructions = request.Instructions,
                        Tools = request.Tools.Count == 0 ? null : [.. request.Tools],
                    },
                    AIContextProviders = request.AttachmentsBlock is null ? [] : [new AttachmentsProvider(request.AttachmentsBlock)],
                })
                .AsBuilder()
                .UseOpenTelemetry(configure: options => options.EnableSensitiveData = captureContent)
                .Build();

            // The history is the whole conversation, so the agent keeps no session: every answer starts from what it is given.
            var asked = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
            await foreach (var update in agent.RunStreamingAsync(request.History, session: null, options: null, cancellationToken))
            {
                foreach (var content in update.Contents)
                {
                    await ObserveAsync(content, run, asked, cancellationToken);
                }
            }

            if (run.Failure is { } failure)
            {
                throw failure;
            }

            if (run.Stop == AnswerStop.TotalTime)
            {
                stop = AgentsTelemetry.StopTotalTime;
                throw TotalTimeExceeded(limits);
            }

            // Calls still unanswered mean the last response asked for tools the answer had no more rounds to run.
            if (run.Stop == AnswerStop.Rounds || asked.Count > 0)
            {
                stop = AgentsTelemetry.StopRounds;
                throw new ModelStreamException(
                    $"The model kept asking for tools after {limits.MaxRounds} rounds, so the answer was stopped. " +
                    "An administrator can raise the limit in the tool settings.");
            }

            stop = AgentsTelemetry.StopCompleted;
            writer.TryComplete();
        }
        catch (OperationCanceledException) when (run.Stop == AnswerStop.TotalTime && !userCancellation.IsCancellationRequested)
        {
            stop = AgentsTelemetry.StopTotalTime;
            writer.TryComplete(TotalTimeExceeded(limits));
        }
        catch (OperationCanceledException exception) when (userCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            // Stopped by the user or by the caller going away: not a failure of the turn (spec 016 D-14).
            stop = AgentsTelemetry.StopCancelled;
            writer.TryComplete(exception);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Whatever ended the answer, the reader must see it: it decides what the stored answer becomes.
            stop ??= AgentsTelemetry.StopError;
            writer.TryComplete(exception);
        }
        finally
        {
            EndTurn(turn, run, limits, stop ?? AgentsTelemetry.StopError);
        }
    }

    /// <summary>Records how the turn ended on its root span: the reason, the counts, and an error status unless the user stopped it (D-13, D-14).</summary>
    private static void EndTurn(Activity? turn, AnswerRun run, ToolLimits limits, string stop)
    {
        if (turn is null)
        {
            return;
        }

        turn.SetTag(AgentsTelemetry.StopReasonTag, stop);
        turn.SetTag(AgentsTelemetry.RoundsTag, stop == AgentsTelemetry.StopRounds ? limits.MaxRounds : run.Rounds);
        turn.SetTag(AgentsTelemetry.ToolCallsTag, run.ToolCalls);

        if (stop != AgentsTelemetry.StopCompleted && stop != AgentsTelemetry.StopCancelled)
        {
            turn.SetStatus(ActivityStatusCode.Error, stop);
        }
    }

    private async Task ObserveAsync(AIContent content, AnswerRun run, Dictionary<string, FunctionCallContent> asked, CancellationToken cancellationToken)
    {
        switch (content)
        {
            case TextContent { Text.Length: > 0 } text:
                await run.PublishAsync(new AnswerChunk(AnswerChunkKind.Text, text.Text), cancellationToken);
                break;

            case TextReasoningContent { Text.Length: > 0 } reasoning:
                await run.PublishAsync(new AnswerChunk(AnswerChunkKind.Reasoning, reasoning.Text), cancellationToken);
                break;

            case FunctionCallContent asking:
                asked[asking.CallId] = asking;
                break;

            case FunctionResultContent result:
                // The invoker publishes the events of the calls it ran. A call to a tool that was not offered never reaches it: the
                // function-invoking client answers the model itself, so it is only logged and shown to the user as a failed call.
                if (asked.Remove(result.CallId, out var call) && !run.ConsumeHandled(result.CallId))
                {
                    LogUnknownTool(logger, call.Name.Length <= 128 ? call.Name : call.Name[..128]);
                    var callId = Guid.CreateVersion7();
                    var message = OpenAiChatClient.ResultText(result);
                    await run.PublishAsync(
                        new AnswerChunk(
                            AnswerChunkKind.ToolCall,
                            string.Empty,
                            new ToolCallEvent(callId, call.Name, OpenAiChatClient.RawArguments(call), RequiresConfirmation: false, ExpiresAt: null)),
                        cancellationToken);
                    await run.PublishAsync(
                        new AnswerChunk(AnswerChunkKind.ToolResult, string.Empty, new ToolResultEvent(callId, ToolResultStatus.Failed, message, message.Length)),
                        cancellationToken);
                }

                break;
        }
    }

    private static ModelStreamException TotalTimeExceeded(ToolLimits limits) =>
        new(
            $"The answer reached the total time limit of {limits.TotalTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds " +
            "for a tool-calling answer and was stopped. An administrator can raise it in the tool settings.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "The model called the tool {Tool}, which was not offered to it")]
    private static partial void LogUnknownTool(ILogger logger, string tool);
}
