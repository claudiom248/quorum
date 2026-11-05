using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quorum.Modules.Agents.Chat;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Telemetry;
using Quorum.Shared.Telemetry;

namespace Quorum.Modules.Agents.Tools;

/// <summary>What the model receives for a call, and how the user is told it went.</summary>
internal sealed record ToolCallResult(ToolResultStatus Status, string Content, IReadOnlyList<ToolSource>? Sources = null);

/// <summary>
/// The <see cref="FunctionInvokingChatClient.FunctionInvoker"/> of an answer: runs one tool call the model asked for. It checks at
/// call time that the tool is still active, validates the arguments, waits for the user's confirmation when the policy asks for
/// it, records the call in the audit log before running it, runs it within the call timeout and the time left, and caps the result.
/// Whatever goes wrong becomes an error result for the model; nothing here throws for a bad call, because the function-invoking
/// client would turn an exception into a vague error.
/// </summary>
internal sealed partial class ToolCallInvoker(
    AgentsDbContext db,
    AgentsDatabaseGate dbGate,
    SubAgentRunner subAgents,
    PendingInteractions interactions,
    TimeProvider clock,
    IOptions<TelemetryOptions> telemetry,
    ILogger<ToolCallInvoker> logger)
{
    internal const int MaxQuestionCharacters = 2000;

    private const int MaxAuditedNameLength = 128;

    /// <exception cref="OperationCanceledException">The user stopped the answer; the call was audited as cancelled.</exception>
    public async ValueTask<object?> InvokeAsync(AnswerRun run, FunctionInvocationContext invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(invocation);

        try
        {
            return await InvokeCoreAsync(run, invocation, cancellationToken);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            // MEAI would turn this into an error result for the model, and its logger is off: end the answer with the failure instead.
            LogInvokerFailed(logger, exception);
            run.Fail(exception);
            invocation.Terminate = true;
            return "The tool was not run because of an internal error.";
        }
    }

    /// <summary>
    /// Ordinary tool calls take the answer's tool gate, so they run one at a time even when the function-invoking client starts the
    /// calls of a round together; delegations do not (spec 013 D-38), they wait for their own gate when they run.
    /// </summary>
    private async ValueTask<object?> InvokeCoreAsync(AnswerRun run, FunctionInvocationContext invocation, CancellationToken cancellationToken)
    {
        var ordinary = invocation.Function is not AgentQuorumTool;
        if (ordinary)
        {
            await run.OrdinaryToolGate.WaitAsync(cancellationToken);
        }

        try
        {
            return await InvokeGatedAsync(run, invocation, cancellationToken);
        }
        finally
        {
            if (ordinary)
            {
                run.OrdinaryToolGate.Release();
            }
        }
    }

    private async ValueTask<object?> InvokeGatedAsync(AnswerRun run, FunctionInvocationContext invocation, CancellationToken cancellationToken)
    {
        var call = invocation.CallContent;
        run.MarkHandled(call.CallId);

        // The client always makes one more request after the last iteration it allows. When the limit leaves no such iteration
        // (one round), a call could never be answered, so it is not run.
        if (invocation.Iteration + 1 >= run.Context.Limits.MaxRounds)
        {
            run.StopFor(AnswerStop.Rounds);
            invocation.Terminate = true;
            return "The tool was not run because the round limit was reached.";
        }

        var callId = Guid.CreateVersion7();
        run.CountToolCall();
        var raw = OpenAiChatClient.RawArguments(call);
        var (auditArguments, truncated) = AuditRedactor.Redact(raw);

        var tool = invocation.Function as QuorumTool;

        // The framework's span of this call is the current one: it gets the Quorum tags, and the audit entry the trace (spec 016 D-3, D-15, D-20).
        var span = Activity.Current;
        var info = new CallInfo(
            callId,
            tool?.Descriptor.ServerName,
            tool?.Descriptor.Name ?? Shorten(call.Name),
            auditArguments,
            truncated,
            clock.GetTimestamp(),
            span,
            TraceIdFor(span, telemetry.Value.Exporting));
        span?.SetTag(AgentsTelemetry.ToolOriginTag, Origin(tool));
        span?.SetTag(AgentsTelemetry.ToolRoundTag, invocation.Iteration + 1);
        span?.SetTag(AgentsTelemetry.ToolArgumentsHashTag, AgentsTelemetry.ArgumentsHash(raw));

        var prepared = await PrepareAsync(run.Context, tool, call, raw, cancellationToken);

        var asksUser = prepared.Refusal is null && prepared.Question is not null;
        var needsConfirmation = prepared.Refusal is null && !asksUser && prepared.Policy == ToolPolicy.Confirm;

        // Everything the wait needs is in place before the call is published: the client may reply, or the time may pass, the moment it sees the event.
        PendingInteraction? pending = null;
        UserWait? wait = null;
        DateTimeOffset? expiresAt = null;
        var holdsDialog = false;
        if (asksUser || needsConfirmation)
        {
            // One dialog at a time (spec 013 D-37); the time waiting for another person's answer is not the model's or the tools'.
            using (run.Deadline.Pause())
            {
                await run.ConfirmationGate.WaitAsync(cancellationToken);
            }

            holdsDialog = true;
            pending = interactions.Register(callId, run.Context.TenantId, run.Context.UserId, run.Context.ConversationId, asksUser ? InteractionKind.Question : InteractionKind.Confirmation);
            wait = new UserWait(new CancellationTokenSource(run.Context.Limits.ConfirmationTimeout, clock), run.Deadline.Pause());
            expiresAt = clock.GetUtcNow() + run.Context.Limits.ConfirmationTimeout;
        }

        using var dialog = holdsDialog ? new GateRelease(run.ConfirmationGate) : null;
        using var _ = wait;

        try
        {
            await run.PublishAsync(
                new AnswerChunk(
                    AnswerChunkKind.ToolCall,
                    string.Empty,
                    new ToolCallEvent(callId, call.Name, raw, needsConfirmation, needsConfirmation ? expiresAt : null, run.ParentCallId, run.AgentSlug, tool is AgentQuorumTool)),
                cancellationToken);

            if (asksUser)
            {
                await run.PublishAsync(
                    new AnswerChunk(AnswerChunkKind.UserQuestion, string.Empty, new UserQuestionEvent(callId, prepared.Question!, expiresAt!.Value)),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (pending is not null)
        {
            // Stopped before the wait began: the call was announced, so it is recorded as cancelled like one stopped while waiting.
            interactions.GiveUp(pending);
            await RecordAsync(run.Context, info, Cancelled(pending.Kind));
            throw;
        }

        ToolCallResult result;
        if (prepared.Refusal is { } refusal)
        {
            result = await RecordAsync(run.Context, info, refusal);
        }
        else if (pending is null)
        {
            result = await ExecuteAsync(run, tool!, call, raw, info, ToolDecision.Auto, cancellationToken);
        }
        else
        {
            // The wait for the person is its own span under the call, so the call's duration is not the time someone takes to decide (D-17).
            using var confirmation = AgentsTelemetry.Source.StartActivity(AgentsTelemetry.ConfirmationActivityName, ActivityKind.Internal);
            var reply = await WaitForReplyAsync(run, info, pending, wait!, cancellationToken);
            confirmation?.SetTag(AgentsTelemetry.ConfirmationDecisionTag, DecisionName(reply));
            result = reply switch
            {
                null => await RecordAsync(run.Context, info, asksUser ? QuestionTimedOut : ConfirmationTimedOut),
                { } answered when asksUser => await RecordAsync(run.Context, info, Answered(answered.Answer!, run.Context.Limits.MaxResultCharacters)),
                { Approved: false } => await RecordAsync(run.Context, info, Denied),
                _ => await RunApprovedAsync(run, tool!, call, raw, info, cancellationToken),
            };
        }

        // The sources go first: a client that shows the result can already match the numbers in it.
        if (result.Sources is { Count: > 0 } sources)
        {
            await run.PublishAsync(
                new AnswerChunk(
                    AnswerChunkKind.Sources,
                    string.Empty,
                    new SourcesEvent(callId, [.. sources.Select(s => new AnswerSource(s.N, s.KnowledgeBase, s.DocumentId, s.Title, s.Position, s.Url))])),
                cancellationToken);
        }

        await run.PublishAsync(
            new AnswerChunk(AnswerChunkKind.ToolResult, string.Empty, new ToolResultEvent(callId, result.Status, result.Content, result.Content.Length, run.ParentCallId, run.AgentSlug)),
            cancellationToken);

        if (run.Deadline.IsExpired)
        {
            // The loop must not ask the model again, and a thrown exception would only reach the model as an error.
            run.StopFor(AnswerStop.TotalTime);
            invocation.Terminate = true;
        }

        if (run.Failure is not null)
        {
            // A sub-agent whose edge says its failure ends the turn: the loop must not ask the model again.
            invocation.Terminate = true;
        }

        return result.Content;
    }

    /// <summary>
    /// Runs a call the user approved. The wait can be long, so the tool is checked again: one that an administrator disabled or took
    /// off the assistant meanwhile must not run on an approval given for what was true before.
    /// </summary>
    private async Task<ToolCallResult> RunApprovedAsync(
        AnswerRun run,
        QuorumTool tool,
        FunctionCallContent call,
        string rawArguments,
        CallInfo info,
        CancellationToken cancellationToken)
    {
        if (await CurrentPolicyAsync(run.Context, tool.Descriptor, cancellationToken) is null)
        {
            return await RecordAsync(
                run.Context,
                info,
                new Verdict(
                    ToolOutcome.Failed,
                    ToolResultStatus.Failed,
                    ToolDecision.Approved,
                    "tool.unavailable",
                    $"The tool '{call.Name}' is no longer available, so it was not run."));
        }

        return await ExecuteAsync(run, tool, call, rawArguments, info, ToolDecision.Approved, cancellationToken);
    }

    /// <summary>The checks at call time that decide whether the call may run. Nothing is written yet.</summary>
    private async Task<Prepared> PrepareAsync(ToolRunContext context, QuorumTool? tool, FunctionCallContent call, string rawArguments, CancellationToken cancellationToken)
    {
        // A delegation is not a stored tool: the edge was read when the turn started, and a turn keeps the definitions it began with (spec 013 AC-25).
        var policy = tool is AgentQuorumTool
            ? ToolPolicy.Auto
            : tool is null ? null : await CurrentPolicyAsync(context, tool.Descriptor, cancellationToken);
        if (tool is null || policy is null)
        {
            return new Prepared(null, Failed("tool.unavailable", $"The tool '{call.Name}' is no longer available."));
        }

        // A call whose arguments are not even JSON arrives with its parse error; the validator names what is wrong with the rest.
        var problem = ToolArgumentValidator.Validate(tool.Descriptor.InputSchema, rawArguments)
            ?? (call.Exception is null ? null : "The arguments are not valid JSON.");
        if (problem is null && tool is AskUserQuorumTool)
        {
            var question = QuestionOf(rawArguments);
            problem = question switch
            {
                { Length: 0 } => "The question is empty.",
                { Length: > MaxQuestionCharacters } => $"The question is too long ({question.Length} characters; the limit is {MaxQuestionCharacters}).",
                _ => null,
            };
            if (problem is null)
            {
                return new Prepared(policy, null, question);
            }
        }

        return problem is not null
            ? new Prepared(policy, Failed("tool.invalid_arguments", $"The arguments for '{call.Name}' are not valid. {problem}"))
            : new Prepared(policy, null);
    }

    private static string QuestionOf(string rawArguments)
    {
        using var document = JsonDocument.Parse(rawArguments);
        return document.RootElement.GetProperty("question").GetString()?.Trim() ?? string.Empty;
    }

    /// <summary>Waits for the user with the answer's clock stopped: the time spent deciding is not the model's or the tools'.</summary>
    /// <returns>The reply, or <see langword="null"/> when the confirmation timeout passed.</returns>
    /// <exception cref="OperationCanceledException">The user stopped the answer; the call was audited as cancelled.</exception>
    private async Task<InteractionReply?> WaitForReplyAsync(AnswerRun run, CallInfo info, PendingInteraction pending, UserWait wait, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wait.Expired);
        try
        {
            return await pending.Reply.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A reply that came in the same instant as the timeout was accepted (the user got a 204), so it is the one that counts.
            return interactions.GiveUp(pending) ? null : pending.Reply.Result;
        }
        catch (OperationCanceledException)
        {
            // Closing the stream, leaving the page and stopping the answer all count as a denial that nothing ran under.
            interactions.GiveUp(pending);
            await RecordAsync(run.Context, info, Cancelled(pending.Kind));
            throw;
        }
        finally
        {
            // The tool, if it runs now, is run against the total time again.
            wait.Resume();
        }
    }

    private async Task<ToolCallResult> ExecuteAsync(
        AnswerRun run,
        QuorumTool tool,
        FunctionCallContent call,
        string rawArguments,
        CallInfo info,
        ToolDecision decision,
        CancellationToken cancellationToken)
    {
        var context = run.Context;
        var entry = ToolAuditEntry.Start(
            context.TenantId,
            context.UserId,
            context.AssistantId,
            context.AgentId,
            context.ConversationId,
            info.CallId,
            info.ServerName,
            info.ToolName,
            decision,
            info.Arguments,
            info.ArgumentsTruncated,
            clock.GetUtcNow(),
            info.TraceId);

        if (!await TryRecordAsync(entry))
        {
            return new ToolCallResult(ToolResultStatus.Failed, "The tool was not run because the call could not be recorded in the audit log.");
        }

        // A delegation runs under the total time of the answer, not the call timeout, which would cut almost every one on a local model (spec 013 D-39).
        var delegation = tool as AgentQuorumTool;
        using var callTimeout = new CancellationTokenSource(delegation is null ? context.Limits.CallTimeout : Timeout.InfiniteTimeSpan, clock);
        using var totalTime = run.Deadline.CreateWatch(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(totalTime.Token, callTimeout.Token);
        using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments);

        ToolOutcome outcome;
        ToolResultStatus status;
        string content;
        int? resultCharacters = null;
        int? sourceCount = null;
        IReadOnlyList<ToolSource>? sources = null;
        string? errorCode = null;

        try
        {
            // WaitAsync makes the timeout hold even for a tool that never looks at its token.
            var produced = delegation is null
                ? await tool.RunWithSourcesAsync(arguments.RootElement, linked.Token).WaitAsync(linked.Token)
                // Not abandoned on cancellation: the sub-agent honours the token, and the answer must not end (and the request's database
                // context go) while its tool calls are still being recorded as cancelled.
                : new ToolOutput(await DelegateAsync(run, delegation, info.CallId, rawArguments, linked.Token));
            var output = produced.Text;
            if (output.Length > context.Limits.MaxResultCharacters)
            {
                outcome = ToolOutcome.Failed;
                status = ToolResultStatus.Failed;
                errorCode = "tool.result_too_large";
                produced.Discard?.Invoke();
                resultCharacters = output.Length;
                content = $"The result is too large ({output.Length} characters; the limit is {context.Limits.MaxResultCharacters}) so it was not returned. " +
                          (delegation is null ? "Call the tool again with narrower arguments." : "Call the agent again and ask for a shorter answer.");
            }
            else
            {
                outcome = ToolOutcome.Succeeded;
                status = ToolResultStatus.Succeeded;
                resultCharacters = output.Length;
                content = output;
                sources = produced.Sources;
                sourceCount = produced.SourceCount ?? sources?.Count;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user stopped the answer (the deadline's own cancellation is a different token).
            await CompleteAsync(entry, ToolOutcome.Cancelled, null, "tool.cancelled", info);
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = ToolOutcome.TimedOut;
            status = ToolResultStatus.TimedOut;
            errorCode = "tool.timed_out";
            content = run.Deadline.IsExpired
                ? "The answer reached its total time limit and the tool was cancelled."
                : $"The tool did not finish in time and was cancelled after {context.Limits.CallTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds.";
        }
        catch (SubAgentAbortException exception)
        {
            // The edge says this failure ends the turn: the result still goes to the ledger, the answer ends with the message.
            outcome = ToolOutcome.Failed;
            status = ToolResultStatus.Failed;
            errorCode = "tool.failed";
            content = exception.Message;
            run.Fail(new ModelStreamException(exception.Message));
        }
        catch (ToolFailedException exception)
        {
            outcome = ToolOutcome.Failed;
            status = ToolResultStatus.Failed;
            errorCode = "tool.failed";
            content = exception.Message;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A tool is a plug-in boundary: whatever it throws must reach the model as an error, not break the answer.
            LogToolCrashed(logger, exception, info.CallId, info.ToolName);
            outcome = ToolOutcome.Failed;
            status = ToolResultStatus.Failed;
            errorCode = "tool.failed";
            content = "The tool failed unexpectedly.";
        }

        await CompleteAsync(entry, outcome, resultCharacters, errorCode, info, sourceCount);
        return new ToolCallResult(status, content, sources);
    }

    /// <summary>
    /// The tool's policy now, or <see langword="null"/> when it is no longer enabled and activated on the agent, its server is no longer
    /// available to it, its definition is not the one the model was offered (an administrator approved another meanwhile), or the
    /// server's address or headers changed since the answer began (an approval is for the address it was given for).
    /// </summary>
    private async Task<ToolPolicy?> CurrentPolicyAsync(ToolRunContext context, ActiveTool tool, CancellationToken cancellationToken)
    {
        var current = await dbGate.RunAsync(
            // One query: the pool is worked out for the owner of the agent inside it, and the activation and the server are in the row.
            async () => await ToolPool.ForAgents(db, [context.AgentId])
                .Where(row => row.Tool.Id == tool.ToolId)
                .Select(row => new { row.Tool.Policy, row.Tool.DefinitionHash, row.ServerUrl, row.ProtectedHeaders })
                .SingleOrDefaultAsync(cancellationToken),
            cancellationToken);

        if (current is null || current.DefinitionHash != tool.DefinitionHash)
        {
            return null;
        }

        return tool.Server is { } offered && (current.ServerUrl != offered.Url || current.ProtectedHeaders != offered.ProtectedHeaders)
            ? null
            : current.Policy;
    }

    /// <summary>Runs the sub-agent once the assistant lets another delegation start (spec 013 D-14).</summary>
    private async Task<string> DelegateAsync(AnswerRun run, AgentQuorumTool delegation, Guid callId, string rawArguments, CancellationToken cancellationToken)
    {
        await run.DelegationGate.WaitAsync(cancellationToken);
        try
        {
            if (run.Failure is not null)
            {
                // An earlier delegation of the round failed and its edge ends the turn: the others do not start.
                throw new ToolFailedException("The turn was ended by another sub-agent, so this one was not run.");
            }

            var task = AgentQuorumTool.TaskOf(rawArguments) ?? throw new ToolFailedException("The task is missing.");
            return await subAgents.RunAsync(this, run, delegation.Agent, callId, task, cancellationToken);
        }
        finally
        {
            run.DelegationGate.Release();
        }
    }

    private async Task<bool> TryRecordAsync(ToolAuditEntry entry)
    {
        try
        {
            // Not cancellable: either the entry exists and the call may run, or neither happens.
            await dbGate.RunAsync(
                async () =>
                {
                    db.ToolAuditEntries.Add(entry);
                    await db.SaveChangesAsync(CancellationToken.None);
                    return true;
                },
                CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Fail closed: any problem recording the call means it does not run (D-28).
            await DetachAsync(entry);
            LogAuditWriteFailed(logger, exception);
            return false;
        }
    }

    /// <summary>Forgets an entry that could not be written, under the gate like every other use of the context.</summary>
    private Task<bool> DetachAsync(ToolAuditEntry entry) =>
        dbGate.RunAsync(
            () =>
            {
                db.Entry(entry).State = EntityState.Detached;
                return Task.FromResult(true);
            },
            CancellationToken.None);

    private async Task CompleteAsync(ToolAuditEntry entry, ToolOutcome outcome, int? resultCharacters, string? errorCode, CallInfo info, int? sourceCount = null)
    {
        var duration = clock.GetElapsedTime(info.Timestamp);
        entry.Complete(outcome, resultCharacters, errorCode, clock.GetUtcNow(), duration, sourceCount: sourceCount);
        TagOutcome(info.Span, outcome, entry.Decision, errorCode);

        try
        {
            // The user may have stopped the answer already; the record must still be completed.
            await dbGate.RunAsync(async () => await db.SaveChangesAsync(CancellationToken.None), CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogAuditCompletionFailed(logger, exception, info.CallId);
        }

        LogCall(logger, info.CallId, info.ToolName, outcome, (int)duration.TotalMilliseconds);
    }

    /// <summary>Records a call that ended without running a tool (refused, denied, unanswered, or a question answered) in one write.</summary>
    private async Task<ToolCallResult> RecordAsync(ToolRunContext context, CallInfo info, Verdict verdict)
    {
        var entry = ToolAuditEntry.Start(
            context.TenantId,
            context.UserId,
            context.AssistantId,
            context.AgentId,
            context.ConversationId,
            info.CallId,
            info.ServerName,
            info.ToolName,
            verdict.Decision,
            info.Arguments,
            info.ArgumentsTruncated,
            clock.GetUtcNow(),
            info.TraceId);
        entry.Complete(verdict.Outcome, verdict.ResultCharacters, verdict.ErrorCode, clock.GetUtcNow(), clock.GetElapsedTime(info.Timestamp));
        TagOutcome(info.Span, verdict.Outcome, verdict.Decision, verdict.ErrorCode);

        try
        {
            await dbGate.RunAsync(
                async () =>
                {
                    db.ToolAuditEntries.Add(entry);
                    await db.SaveChangesAsync(CancellationToken.None);
                    return true;
                },
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing ran, so there is nothing to fail closed on: the model still gets its answer.
            await DetachAsync(entry);
            LogAuditWriteFailed(logger, exception);
        }

        var duration = clock.GetElapsedTime(info.Timestamp);
        LogCall(logger, info.CallId, info.ToolName, verdict.Outcome, (int)duration.TotalMilliseconds);
        return new ToolCallResult(verdict.Status, verdict.Message);
    }

    private static Verdict Failed(string errorCode, string message) =>
        new(ToolOutcome.Failed, ToolResultStatus.Failed, null, errorCode, message);

    /// <summary>The user's answer is a tool result like any other: one over the limit is not truncated but turned into an error (D-10).</summary>
    private static Verdict Answered(string answer, int maxCharacters) =>
        answer.Length > maxCharacters
            ? new Verdict(
                ToolOutcome.Failed,
                ToolResultStatus.Failed,
                ToolDecision.Auto,
                "tool.result_too_large",
                $"The user's answer is too large ({answer.Length} characters; the limit is {maxCharacters}) so it was not returned. " +
                "Ask the question again, asking for a shorter answer.",
                answer.Length)
            : new Verdict(ToolOutcome.Succeeded, ToolResultStatus.Succeeded, ToolDecision.Auto, null, answer, answer.Length);

    private static Verdict Cancelled(InteractionKind kind) =>
        new(ToolOutcome.Cancelled, ToolResultStatus.Cancelled, kind == InteractionKind.Confirmation ? ToolDecision.Denied : ToolDecision.Auto, "tool.cancelled", "The answer was stopped.");

    private static string Shorten(string name) => name.Length <= MaxAuditedNameLength ? name : name[..MaxAuditedNameLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool call {CallId} to {Tool} ended {Outcome} after {DurationMs} ms")]
    private static partial void LogCall(ILogger logger, Guid callId, string tool, ToolOutcome outcome, int durationMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "The audit entry of a tool call could not be written, so the call was not run")]
    private static partial void LogAuditWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The audit entry of tool call {CallId} could not be completed")]
    private static partial void LogAuditCompletionFailed(ILogger logger, Exception exception, Guid callId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The tool-call invoker failed, so the answer was stopped")]
    private static partial void LogInvokerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tool call {CallId} to {Tool} threw an exception")]
    private static partial void LogToolCrashed(ILogger logger, Exception exception, Guid callId, string tool);

    private sealed record CallInfo(
        Guid CallId,
        string? ServerName,
        string ToolName,
        string Arguments,
        bool ArgumentsTruncated,
        long Timestamp,
        Activity? Span,
        string? TraceId);

    /// <summary>
    /// The trace id for the audit entry: only for a trace that is recorded, since one that a sampler drops is never exported and the
    /// link from the audit page would find nothing (spec 016 D-11, D-20).
    /// </summary>
    internal static string? TraceIdFor(Activity? span, bool exporting) =>
        exporting && span is { Recorded: true } ? span.TraceId.ToHexString() : null;

    private static string Origin(QuorumTool? tool) => tool switch
    {
        null => "unknown",
        AgentQuorumTool => "agent",
        _ when tool.Descriptor.ServerName is not null => "mcp",
        _ => "builtin",
    };

    private static string DecisionName(InteractionReply? reply) => reply switch
    {
        null => "timed_out",
        { Answer: not null } => "answered",
        { Approved: true } => "approved",
        _ => "denied",
    };

    /// <summary>The outcome, the decision and the error code on the call's span; a failed or timed-out call is an error on it (spec 016 D-14).</summary>
    private static void TagOutcome(Activity? span, ToolOutcome outcome, ToolDecision? decision, string? errorCode)
    {
        if (span is null)
        {
            return;
        }

        span.SetTag(AgentsTelemetry.ToolOutcomeTag, outcome.ToString());
        if (decision is { } taken)
        {
            span.SetTag(AgentsTelemetry.ToolDecisionTag, taken.ToString());
        }

        if (errorCode is not null)
        {
            span.SetTag(AgentsTelemetry.ErrorCodeTag, errorCode);
        }

        if (outcome is ToolOutcome.Failed or ToolOutcome.TimedOut)
        {
            span.SetStatus(ActivityStatusCode.Error, errorCode);
        }
    }

    /// <summary>How a call ended without a tool running (or, for a question, without one to run).</summary>
    private sealed record Verdict(ToolOutcome Outcome, ToolResultStatus Status, ToolDecision? Decision, string? ErrorCode, string Message, int? ResultCharacters = null);

    private static readonly Verdict Denied = new(
        ToolOutcome.Denied,
        ToolResultStatus.Denied,
        ToolDecision.Denied,
        "tool.denied",
        "The call was denied by the user, so the tool was not run.");

    private static readonly Verdict ConfirmationTimedOut = new(
        ToolOutcome.TimedOut,
        ToolResultStatus.TimedOut,
        ToolDecision.TimedOut,
        "tool.confirmation_timed_out",
        "The user did not respond in time, which counts as denied by the user, so the tool was not run.");

    private static readonly Verdict QuestionTimedOut = new(
        ToolOutcome.TimedOut,
        ToolResultStatus.TimedOut,
        ToolDecision.TimedOut,
        "tool.question_timed_out",
        "The user did not answer the question in time.");

    /// <summary>Gives the dialog gate back when the call that held it is over, however it ended.</summary>
    private sealed class GateRelease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>What a wait for the user holds: the time after which it counts as unanswered, and the stopped clock of the answer.</summary>
    private sealed class UserWait(CancellationTokenSource expiry, IDisposable pause) : IDisposable
    {
        public CancellationToken Expired => expiry.Token;

        public void Resume() => pause.Dispose();

        public void Dispose()
        {
            pause.Dispose();
            expiry.Dispose();
        }
    }

    private sealed record Prepared(ToolPolicy? Policy, Verdict? Refusal, string? Question = null);
}
