using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Chat;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>What the model receives for a call, and how the user is told it went.</summary>
internal sealed record ToolCallResult(ToolResultStatus Status, string Content);

/// <summary>
/// Runs one tool call the model asked for: finds the tool, checks at call time that it is still active, validates the arguments,
/// records the call in the audit log before running it, runs it within the call timeout, and caps the result. Whatever goes
/// wrong becomes an error result for the model; nothing here throws for a bad call.
/// </summary>
internal sealed partial class ToolExecutor(
    AgentsDbContext db,
    IEnumerable<INativeTool> nativeTools,
    TimeProvider clock,
    ILogger<ToolExecutor> logger)
{
    private const int MaxAuditedNameLength = 128;

    private readonly Dictionary<string, INativeTool> _nativeTools = nativeTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    /// <param name="cancellationToken">Stops the call: the user left, or the answer ran out of total time.</param>
    /// <param name="userCancellation">The part of <paramref name="cancellationToken"/> that is the user stopping the answer.</param>
    /// <exception cref="OperationCanceledException">The user stopped the answer; the call was audited as cancelled.</exception>
    public async Task<ToolCallResult> ExecuteAsync(
        ModelToolCall call,
        Guid callId,
        ToolRunContext context,
        CancellationToken cancellationToken,
        CancellationToken userCancellation)
    {
        var tool = context.Tools.FirstOrDefault(t => t.ExposedName == call.Name);
        var (auditArguments, truncated) = AuditRedactor.Redact(call.Arguments);
        var info = new CallInfo(callId, tool?.ServerName, tool?.Name ?? Shorten(call.Name), auditArguments, truncated, clock.GetTimestamp());

        if (tool is null)
        {
            var names = string.Join(", ", context.Tools.Select(t => t.ExposedName));
            return await RefuseAsync(context, info, ToolOutcome.Failed, ToolResultStatus.Failed, null, "tool.unknown", $"Unknown tool '{call.Name}'. The available tools are: {names}.");
        }

        var policy = await CurrentPolicyAsync(context, tool, cancellationToken);
        if (policy is null || (tool.Origin == ToolOrigin.Native && !_nativeTools.ContainsKey(tool.Name)))
        {
            return await RefuseAsync(context, info, ToolOutcome.Failed, ToolResultStatus.Failed, null, "tool.unavailable", $"The tool '{call.Name}' is no longer available.");
        }

        var problem = ToolArgumentValidator.Validate(tool.InputSchema, call.Arguments);
        if (problem is not null)
        {
            return await RefuseAsync(context, info, ToolOutcome.Failed, ToolResultStatus.Failed, null, "tool.invalid_arguments", $"The arguments for '{call.Name}' are not valid. {problem}");
        }

        if (policy == ToolPolicy.Confirm)
        {
            // Asking the user comes with the confirmation flow; until then a tool that needs a yes never runs.
            return await RefuseAsync(
                context,
                info,
                ToolOutcome.Denied,
                ToolResultStatus.Denied,
                ToolDecision.Denied,
                "tool.confirmation_unavailable",
                $"The tool '{call.Name}' needs the user's confirmation, which is not available yet, so it was not run.");
        }

        var entry = ToolAuditEntry.Start(
            context.TenantId,
            context.UserId,
            context.AssistantId,
            context.ConversationId,
            callId,
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

        return await RunAsync(tool, call, entry, info, context, cancellationToken, userCancellation);
    }

    private async Task<ToolCallResult> RunAsync(
        ActiveTool tool,
        ModelToolCall call,
        ToolAuditEntry entry,
        CallInfo info,
        ToolRunContext context,
        CancellationToken cancellationToken,
        CancellationToken userCancellation)
    {
        using var callTimeout = new CancellationTokenSource(context.Limits.CallTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, callTimeout.Token);
        using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);

        ToolOutcome outcome;
        ToolResultStatus status;
        string content;
        int? resultCharacters = null;
        string? errorCode = null;

        try
        {
            // WaitAsync makes the timeout hold even for a tool that never looks at its token.
            var output = await Execute(tool, arguments.RootElement, linked.Token).WaitAsync(linked.Token);
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
        catch (OperationCanceledException) when (userCancellation.IsCancellationRequested)
        {
            await CompleteAsync(entry, ToolOutcome.Cancelled, null, "tool.cancelled", info);
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = ToolOutcome.TimedOut;
            status = ToolResultStatus.TimedOut;
            errorCode = "tool.timed_out";
            content = callTimeout.IsCancellationRequested
                ? $"The tool did not finish in time and was cancelled after {context.Limits.CallTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds."
                : "The answer reached its total time limit and the tool was cancelled.";
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

    private Task<string> Execute(ActiveTool tool, JsonElement arguments, CancellationToken cancellationToken) =>
        _nativeTools[tool.Name].ExecuteAsync(arguments, cancellationToken);

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

    private async Task<ToolCallResult> RefuseAsync(
        ToolRunContext context,
        CallInfo info,
        ToolOutcome outcome,
        ToolResultStatus status,
        ToolDecision? decision,
        string errorCode,
        string message)
    {
        var entry = ToolAuditEntry.Start(
            context.TenantId,
            context.UserId,
            context.AssistantId,
            context.ConversationId,
            info.CallId,
            info.ServerName,
            info.ToolName,
            decision,
            info.Arguments,
            info.ArgumentsTruncated,
            clock.GetUtcNow());
        entry.Complete(outcome, null, errorCode, clock.GetUtcNow(), clock.GetElapsedTime(info.Timestamp));

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

        LogCall(logger, info.CallId, info.ToolName, outcome, 0);
        return new ToolCallResult(status, message);
    }

    private static string Shorten(string name) => name.Length <= MaxAuditedNameLength ? name : name[..MaxAuditedNameLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool call {CallId} to {Tool} ended {Outcome} after {DurationMs} ms")]
    private static partial void LogCall(ILogger logger, Guid callId, string tool, ToolOutcome outcome, int durationMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "The audit entry of a tool call could not be written, so the call was not run")]
    private static partial void LogAuditWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The audit entry of tool call {CallId} could not be completed")]
    private static partial void LogAuditCompletionFailed(ILogger logger, Exception exception, Guid callId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tool call {CallId} to {Tool} threw an exception")]
    private static partial void LogToolCrashed(ILogger logger, Exception exception, Guid callId, string tool);

    private sealed record CallInfo(Guid CallId, string? ServerName, string ToolName, string Arguments, bool ArgumentsTruncated, long Timestamp);
}
