using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Chat;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>What the model receives for a call, and how the user is told it went.</summary>
internal sealed record ToolCallResult(ToolResultStatus Status, string Content);

/// <summary>
/// The <see cref="FunctionInvokingChatClient.FunctionInvoker"/> of an answer: runs one tool call the model asked for. It checks at
/// call time that the tool is still active, validates the arguments, records the call in the audit log before running it, runs
/// it within the call timeout and the time left, and caps the result. Whatever goes wrong becomes an error result for the model;
/// nothing here throws for a bad call, because the function-invoking client would turn an exception into a vague error.
/// </summary>
internal sealed partial class ToolCallInvoker(AgentsDbContext db, TimeProvider clock, ILogger<ToolCallInvoker> logger)
{
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

    private async ValueTask<object?> InvokeCoreAsync(AnswerRun run, FunctionInvocationContext invocation, CancellationToken cancellationToken)
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
        var raw = OpenAiChatClient.RawArguments(call);
        var (auditArguments, truncated) = AuditRedactor.Redact(raw);

        var tool = invocation.Function as QuorumTool;
        var info = new CallInfo(callId, tool?.Descriptor.ServerName, tool?.Descriptor.Name ?? Shorten(call.Name), auditArguments, truncated, clock.GetTimestamp());

        var prepared = await PrepareAsync(run.Context, tool, call, raw, cancellationToken);

        // Where a call that needs the user's confirmation is published, with its expiry, before the answer waits for the decision.
        await run.PublishAsync(
            new AnswerChunk(
                AnswerChunkKind.ToolCall,
                string.Empty,
                new ToolCallEvent(callId, call.Name, raw, RequiresConfirmation: false, ExpiresAt: null)),
            cancellationToken);

        var result = prepared.Refusal is { } refusal
            ? await RefuseAsync(run.Context, info, refusal)
            : await ExecuteAsync(run, tool!, call, raw, info, cancellationToken);

        await run.PublishAsync(
            new AnswerChunk(AnswerChunkKind.ToolResult, string.Empty, new ToolResultEvent(callId, result.Status, result.Content, result.Content.Length)),
            cancellationToken);

        if (run.Deadline.IsExpired)
        {
            // The loop must not ask the model again, and a thrown exception would only reach the model as an error.
            run.StopFor(AnswerStop.TotalTime);
            invocation.Terminate = true;
        }

        return result.Content;
    }

    /// <summary>The checks at call time that decide whether the call may run. Nothing is written yet.</summary>
    private async Task<Prepared> PrepareAsync(ToolRunContext context, QuorumTool? tool, FunctionCallContent call, string rawArguments, CancellationToken cancellationToken)
    {
        var policy = tool is null ? null : await CurrentPolicyAsync(context, tool.Descriptor, cancellationToken);
        if (tool is null || policy is null)
        {
            return new Prepared(null, new Refusal(ToolOutcome.Failed, ToolResultStatus.Failed, null, "tool.unavailable", $"The tool '{call.Name}' is no longer available."));
        }

        // A call whose arguments are not even JSON arrives with its parse error; the validator names what is wrong with the rest.
        var problem = ToolArgumentValidator.Validate(tool.Descriptor.InputSchema, rawArguments)
            ?? (call.Exception is null ? null : "The arguments are not valid JSON.");
        if (problem is not null)
        {
            return new Prepared(policy, new Refusal(ToolOutcome.Failed, ToolResultStatus.Failed, null, "tool.invalid_arguments", $"The arguments for '{call.Name}' are not valid. {problem}"));
        }

        if (policy == ToolPolicy.Confirm)
        {
            // Asking the user comes with the confirmation flow; until then a tool that needs a yes never runs.
            return new Prepared(
                policy,
                new Refusal(
                    ToolOutcome.Denied,
                    ToolResultStatus.Denied,
                    ToolDecision.Denied,
                    "tool.confirmation_unavailable",
                    $"The tool '{call.Name}' needs the user's confirmation, which is not available yet, so it was not run."));
        }

        return new Prepared(policy, null);
    }

    private async Task<ToolCallResult> ExecuteAsync(
        AnswerRun run,
        QuorumTool tool,
        FunctionCallContent call,
        string rawArguments,
        CallInfo info,
        CancellationToken cancellationToken)
    {
        var context = run.Context;
        var entry = ToolAuditEntry.Start(
            context.TenantId,
            context.UserId,
            context.AssistantId,
            context.ConversationId,
            info.CallId,
            info.ServerName,
            info.ToolName,
            ToolDecision.Auto,
            info.Arguments,
            info.ArgumentsTruncated,
            clock.GetUtcNow());

        if (!await TryRecordAsync(entry))
        {
            return new ToolCallResult(ToolResultStatus.Failed, "The tool was not run because the call could not be recorded in the audit log.");
        }

        using var callTimeout = new CancellationTokenSource(context.Limits.CallTimeout, clock);
        using var totalTime = run.Deadline.CreateWatch(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(totalTime.Token, callTimeout.Token);
        using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments);

        ToolOutcome outcome;
        ToolResultStatus status;
        string content;
        int? resultCharacters = null;
        string? errorCode = null;

        try
        {
            // WaitAsync makes the timeout hold even for a tool that never looks at its token.
            var output = await tool.RunAsync(arguments.RootElement, linked.Token).WaitAsync(linked.Token);
            if (output.Length > context.Limits.MaxResultCharacters)
            {
                outcome = ToolOutcome.Failed;
                status = ToolResultStatus.Failed;
                errorCode = "tool.result_too_large";
                resultCharacters = output.Length;
                content = $"The result is too large ({output.Length} characters; the limit is {context.Limits.MaxResultCharacters}) so it was not returned. " +
                          "Call the tool again with narrower arguments.";
            }
            else
            {
                outcome = ToolOutcome.Succeeded;
                status = ToolResultStatus.Succeeded;
                resultCharacters = output.Length;
                content = output;
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

        await CompleteAsync(entry, outcome, resultCharacters, errorCode, info);
        return new ToolCallResult(status, content);
    }

    /// <returns>The tool's policy now, or <see langword="null"/> when it is no longer enabled and activated on the assistant.</returns>
    private async Task<ToolPolicy?> CurrentPolicyAsync(ToolRunContext context, ActiveTool tool, CancellationToken cancellationToken) =>
        await (
            from candidate in ToolPool.For(db)
            where candidate.Id == tool.ToolId
                  && db.AssistantTools.Any(a => a.ToolId == candidate.Id && a.AssistantId == context.AssistantId)
            select (ToolPolicy?)candidate.Policy)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<bool> TryRecordAsync(ToolAuditEntry entry)
    {
        try
        {
            db.ToolAuditEntries.Add(entry);
            // Not cancellable: either the entry exists and the call may run, or neither happens.
            await db.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Fail closed: any problem recording the call means it does not run (D-28).
            db.Entry(entry).State = EntityState.Detached;
            LogAuditWriteFailed(logger, exception);
            return false;
        }
    }

    private async Task CompleteAsync(ToolAuditEntry entry, ToolOutcome outcome, int? resultCharacters, string? errorCode, CallInfo info)
    {
        var duration = clock.GetElapsedTime(info.Timestamp);
        entry.Complete(outcome, resultCharacters, errorCode, clock.GetUtcNow(), duration);

        try
        {
            // The user may have stopped the answer already; the record must still be completed.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogAuditCompletionFailed(logger, exception, info.CallId);
        }

        LogCall(logger, info.CallId, info.ToolName, outcome, (int)duration.TotalMilliseconds);
    }

    private async Task<ToolCallResult> RefuseAsync(ToolRunContext context, CallInfo info, Refusal refusal)
    {
        var entry = ToolAuditEntry.Start(
            context.TenantId,
            context.UserId,
            context.AssistantId,
            context.ConversationId,
            info.CallId,
            info.ServerName,
            info.ToolName,
            refusal.Decision,
            info.Arguments,
            info.ArgumentsTruncated,
            clock.GetUtcNow());
        entry.Complete(refusal.Outcome, null, refusal.ErrorCode, clock.GetUtcNow(), clock.GetElapsedTime(info.Timestamp));

        try
        {
            db.ToolAuditEntries.Add(entry);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing ran, so there is nothing to fail closed on: the model still gets its answer.
            db.Entry(entry).State = EntityState.Detached;
            LogAuditWriteFailed(logger, exception);
        }

        LogCall(logger, info.CallId, info.ToolName, refusal.Outcome, 0);
        return new ToolCallResult(refusal.Status, refusal.Message);
    }

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

    private sealed record CallInfo(Guid CallId, string? ServerName, string ToolName, string Arguments, bool ArgumentsTruncated, long Timestamp);

    private sealed record Refusal(ToolOutcome Outcome, ToolResultStatus Status, ToolDecision? Decision, string ErrorCode, string Message);

    private sealed record Prepared(ToolPolicy? Policy, Refusal? Refusal);
}
