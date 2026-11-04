using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Tools;
using Quorum.Shared.Telemetry;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// Runs a sub-agent for a delegation (spec 013 D-3): a <see cref="ChatClientAgent"/> over its own model client and its own
/// function-invoking client, with the child's tools only, that sees the task and nothing of the conversation (D-4) and returns its
/// final message. Its tool calls go through the same <see cref="ToolCallInvoker"/> as the root agent's, on a run of their own that
/// shares the answer's events, deadline and gates, so they are audited, limited and shown nested under the delegation call (D-11, D-28).
/// </summary>
internal sealed partial class SubAgentRunner(IHttpClientFactory httpClientFactory, IOptions<TelemetryOptions> telemetry, ILogger<SubAgentRunner> logger)
{
    /// <returns>The final message of the sub-agent.</returns>
    /// <exception cref="ToolFailedException">The sub-agent failed and its edge returns the failure to the caller (D-13).</exception>
    /// <exception cref="SubAgentAbortException">The sub-agent failed and its edge ends the turn (D-13).</exception>
    /// <exception cref="OperationCanceledException">The answer was stopped or ran out of time (D-22, D-39).</exception>
    public async Task<string> RunAsync(ToolCallInvoker invoker, AnswerRun parent, SubAgentDefinition agent, Guid callId, string task, CancellationToken cancellationToken)
    {
        var run = parent.ForSubAgent(agent.AgentId, agent.Slug, callId, ContextWindow.CharacterBudget(agent.ContextTokenBudget));
        try
        {
            return await RunCoreAsync(invoker, run, agent, task, cancellationToken);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            var message = exception is ModelStreamException or ToolFailedException
                ? exception.Message
                : "The sub-agent failed unexpectedly.";
            if (exception is not (ModelStreamException or ToolFailedException))
            {
                LogFailed(logger, exception, agent.Slug, callId);
            }

            var text = $"The sub-agent '{agent.Slug}' failed: {message}";
            throw agent.FailurePolicy == SubAgentFailurePolicy.AbortTurn
                ? new SubAgentAbortException(text, exception)
                : new ToolFailedException(text, exception);
        }
    }

    private async Task<string> RunCoreAsync(ToolCallInvoker invoker, AnswerRun run, SubAgentDefinition agent, string task, CancellationToken cancellationToken)
    {
        var limits = run.Context.Limits;

        // The first request is measured here, as the root agent's is before its answer starts; later requests are the guard's.
        if (agent.Instructions.Length + agent.ToolsCharacters + task.Length > run.UsableCharacters)
        {
            throw new ModelStreamException("The task, the instructions and the tools of the sub-agent do not fit the context of its model.");
        }

        var captureContent = telemetry.Value.CaptureContent;
        using var model = new OpenAiChatClient(httpClientFactory, agent.BaseUrl, agent.ModelName, agent.ApiKey, offersTools: agent.Tools.Count > 0);
        using var traced = new OpenTelemetryChatClient(new ContextGuardChatClient(model, run)) { EnableSensitiveData = captureContent };
        using var tools = new FunctionInvokingChatClient(traced)
        {
            AllowConcurrentInvocation = false,
            MaximumIterationsPerRequest = Math.Max(1, limits.MaxRounds - 1),
            FunctionInvoker = (invocation, token) => invoker.InvokeAsync(run, invocation, token),
        };

        // The run of a sub-agent is an agent span of its own under the delegation call (spec 016 D-16).
        var chatAgent = new ChatClientAgent(
            tools,
            new ChatClientAgentOptions
            {
                UseProvidedChatClientAsIs = true,
                ChatOptions = new ChatOptions
                {
                    Instructions = agent.Instructions,
                    Tools = agent.Tools.Count == 0 ? null : [.. agent.Tools],
                },
            })
            .AsBuilder()
            .UseOpenTelemetry(configure: options => options.EnableSensitiveData = captureContent)
            .Build();

        // Only the final message is the result: the text the sub-agent writes before a tool call is thinking aloud, and none of it
        // reaches the user (D-32). The text is dropped at each tool call, so what is left at the end is the final message.
        var round = new StringBuilder();
        var asked = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        await foreach (var update in chatAgent.RunStreamingAsync([new ChatMessage(ChatRole.User, task)], session: null, options: null, cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        round.Append(text.Text);
                        break;

                    case FunctionCallContent asking:
                        asked[asking.CallId] = asking;
                        round.Clear();
                        break;

                    case FunctionResultContent result:
                        // A call the invoker never took was to a tool the sub-agent was not offered: show it to the user as a failed call.
                        if (asked.Remove(result.CallId, out var call) && !run.ConsumeHandled(result.CallId))
                        {
                            var unknownId = Guid.CreateVersion7();
                            var message = OpenAiChatClient.ResultText(result);
                            await run.PublishAsync(
                                new AnswerChunk(
                                    AnswerChunkKind.ToolCall,
                                    string.Empty,
                                    new ToolCallEvent(unknownId, call.Name, OpenAiChatClient.RawArguments(call), false, null, run.ParentCallId, run.AgentSlug)),
                                cancellationToken);
                            await run.PublishAsync(
                                new AnswerChunk(
                                    AnswerChunkKind.ToolResult,
                                    string.Empty,
                                    new ToolResultEvent(unknownId, ToolResultStatus.Failed, message, message.Length, run.ParentCallId, run.AgentSlug)),
                                cancellationToken);
                        }

                        break;
                }
            }
        }

        if (run.Failure is { } failure)
        {
            throw failure;
        }

        if (run.Stop == AnswerStop.TotalTime)
        {
            throw new OperationCanceledException("The answer reached its total time limit.");
        }

        if (run.Stop == AnswerStop.Rounds || asked.Count > 0)
        {
            throw new ModelStreamException($"The sub-agent kept asking for tools after {limits.MaxRounds} rounds, so it was stopped.");
        }

        var final = round.ToString().Trim();
        return final.Length == 0 ? throw new ModelStreamException("The sub-agent finished without an answer.") : final;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The sub-agent {Agent} called by {CallId} failed unexpectedly")]
    private static partial void LogFailed(ILogger logger, Exception exception, string agent, Guid callId);
}
