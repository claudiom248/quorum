using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

// Option "own loop as an IChatClient middleware": the tool loop of PR #30 re-expressed as a DelegatingChatClient on top of
// Microsoft.Extensions.AI types. No FunctionInvokingChatClient, no side channel: the events are ordinary updates in order.

enum CallStatus { Succeeded, Failed, Denied, Cancelled, TimedOut }

/// <summary>Raised when a limit of the loop ends the answer (rounds, total time, context). The message is for the user.</summary>
sealed class AnswerLimitException(string message) : Exception(message);

/// <summary>A deadline that does not run while the answer waits for a person (the confirmation wait is not "model time").</summary>
sealed class PausableDeadline(TimeProvider clock, TimeSpan total)
{
    private long _pausedAt;
    private TimeSpan _paused;
    private readonly long _start = clock.GetTimestamp();

    public TimeSpan Remaining => total - (clock.GetElapsedTime(_start) - _paused - (_pausedAt != 0 ? clock.GetElapsedTime(_pausedAt) : TimeSpan.Zero));

    public void Pause() => _pausedAt = clock.GetTimestamp();

    public void Resume()
    {
        _paused += clock.GetElapsedTime(_pausedAt);
        _pausedAt = 0;
    }
}

sealed class ToolLoopChatClient(IChatClient inner, AnswerContext ctx) : DelegatingChatClient(inner)
{
    public const string CallIdKey = "quorum.callId";
    public const string RequiresConfirmationKey = "quorum.requiresConfirmation";
    public const string ExpiresAtKey = "quorum.expiresAt";
    public const string StatusKey = "quorum.status";

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var limits = ctx.Limits;
        var deadline = new PausableDeadline(ctx.Clock, limits.TotalTimeout);
        var history = messages.ToList();
        var tools = (options?.Tools ?? []).OfType<AIFunction>().ToDictionary(t => t.Name, StringComparer.Ordinal);
        var characters = history.Sum(Length);

        for (var round = 1; round <= limits.MaxRounds; round++)
        {
            // Total time: a token that fires when the remaining model time is used up; the user's token is kept apart.
            using var total = new CancellationTokenSource(Positive(deadline.Remaining), ctx.Clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, total.Token);

            var text = new System.Text.StringBuilder();
            var calls = new List<FunctionCallContent>();
            var stream = inner.GetStreamingResponseAsync(history, options, linked.Token).GetAsyncEnumerator(linked.Token);
            try
            {
                while (true)
                {
                    bool moved;
                    try { moved = await stream.MoveNextAsync(); }
                    catch (OperationCanceledException) when (total.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new AnswerLimitException($"The answer reached the total time limit of {limits.TotalTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds and was stopped.");
                    }

                    if (!moved) break;
                    var update = stream.Current;
                    foreach (var call in update.Contents.OfType<FunctionCallContent>()) calls.Add(call);
                    foreach (var t in update.Contents.OfType<TextContent>()) text.Append(t.Text);
                    update.Contents = [.. update.Contents.Where(c => c is not FunctionCallContent)];
                    if (update.Contents.Count > 0) yield return update;
                }
            }
            finally { await stream.DisposeAsync(); }

            if (calls.Count == 0) yield break;

            if (round == limits.MaxRounds)
            {
                throw new AnswerLimitException($"The model kept asking for tools after {limits.MaxRounds} rounds, so the answer was stopped.");
            }

            history.Add(new ChatMessage(ChatRole.Assistant, [.. (text.Length > 0 ? [new TextContent(text.ToString())] : Array.Empty<AIContent>()), .. calls]));

            foreach (var call in calls)
            {
                var callId = Guid.CreateVersion7();
                tools.TryGetValue(call.Name, out var function);

                // Phase 1: call-time checks. Tells the user (and us) whether this call will wait for a decision.
                var prepared = await Executor.PrepareAsync(ctx, function, call, callId);
                var started = new ChatResponseUpdate(ChatRole.Assistant, [call]);
                started.Contents[0].AdditionalProperties = new() { [CallIdKey] = callId, [RequiresConfirmationKey] = prepared.RequiresConfirmation, [ExpiresAtKey] = prepared.ExpiresAt };
                yield return started;

                // Phase 2: confirmation wait (not counted in the total time), audit, run.
                if (prepared.RequiresConfirmation) deadline.Pause();
                CallOutcome result;
                try { result = await Executor.RunAsync(ctx, prepared, cancellationToken, () => deadline.Remaining); }
                finally { if (prepared.RequiresConfirmation) deadline.Resume(); }

                var finished = new ChatResponseUpdate(ChatRole.Tool, [new FunctionResultContent(call.CallId, result.Content)]);
                finished.Contents[0].AdditionalProperties = new() { [CallIdKey] = callId, [StatusKey] = result.Status.ToString() };
                yield return finished;

                history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result.Content)]));
                characters = history.Sum(Length);
                if (characters > limits.UsableCharacters)
                {
                    throw new AnswerLimitException("The tool results filled the context of the model, so the answer was stopped.");
                }
            }
        }
    }

    private static TimeSpan Positive(TimeSpan span) => span > TimeSpan.Zero ? span : TimeSpan.FromMilliseconds(1);

    private static int Length(ChatMessage m) => m.Contents.Sum(c => c switch
    {
        TextContent t => t.Text.Length,
        FunctionResultContent r => r.Result?.ToString()?.Length ?? 0,
        FunctionCallContent f => JsonSerializer.Serialize(f.Arguments).Length + f.Name.Length,
        _ => 0,
    });

}

sealed record Prepared(AIFunction? Function, FunctionCallContent Call, Guid CallId, Policy? Policy, string? Refusal, string? RefusalCode, bool RequiresConfirmation, DateTimeOffset? ExpiresAt, AuditEntry Entry);

sealed record CallOutcome(CallStatus Status, string Content);

/// <summary>The two-phase executor PR 3 needs anyway (it must publish tool_call, with requiresConfirmation, BEFORE waiting).</summary>
static class Executor
{
    public static async ValueTask<Prepared> PrepareAsync(AnswerContext ctx, AIFunction? function, FunctionCallContent call, Guid callId)
    {
        var args = JsonSerializer.Serialize(call.Arguments);
        var entry = new AuditEntry(callId, call.Name, args, null);
        if (function is null)
        {
            return new(null, call, callId, null, $"Unknown tool '{call.Name}'. The available tools are: ...", "tool.unknown", false, null, entry);
        }

        var policy = await ctx.Store.CurrentAsync(call.Name);
        if (policy is null) return new(function, call, callId, null, $"The tool '{call.Name}' is no longer available.", "tool.unavailable", false, null, entry);

        if (function.JsonSchema.TryGetProperty("required", out var required))
            foreach (var r in required.EnumerateArray())
                if (call.Arguments?.ContainsKey(r.GetString()!) != true)
                    return new(function, call, callId, policy, $"The arguments for '{call.Name}' are not valid. Missing required argument '{r.GetString()}'.", "tool.invalid_arguments", false, null, entry);

        var confirm = policy == Policy.Confirm;
        return new(function, call, callId, policy, null, null, confirm, confirm ? ctx.Clock.GetUtcNow() + ctx.Limits.ConfirmationTimeout : null, entry);
    }

    public static async ValueTask<CallOutcome> RunAsync(AnswerContext ctx, Prepared p, CancellationToken userCancellation, Func<TimeSpan> remaining)
    {
        if (p.Refusal is not null)
        {
            ctx.Store.Log.Add($"refused:{p.RefusalCode}:{p.Call.Name}");
            return new(CallStatus.Failed, p.Refusal);
        }

        var entry = p.Entry;
        if (p.RequiresConfirmation)
        {
            string decision;
            try { decision = await ctx.Pending.RegisterAsync(p.CallId, ctx.Limits.ConfirmationTimeout, userCancellation) ? "Approved" : "Denied"; }
            catch (TimeoutException) { decision = "TimedOut"; }

            entry = entry with { Decision = decision };
            if (decision != "Approved")
            {
                await ctx.Store.StartAsync(entry);
                ctx.Store.Log.Add($"refused:tool.denied:{p.Call.Name}");
                return new(CallStatus.Denied, decision == "Denied" ? "The user denied the call." : "The user did not answer in time.");
            }
        }
        else
        {
            entry = entry with { Decision = "Auto" };
        }

        try { await ctx.Store.StartAsync(entry); }
        catch (Exception)
        {
            ctx.Store.Log.Add("refused:audit.failed:" + p.Call.Name);
            return new(CallStatus.Failed, "The tool was not run because the call could not be recorded in the audit log.");
        }

        var allowed = ctx.Limits.CallTimeout < remaining() ? ctx.Limits.CallTimeout : remaining();
        using var callTimeout = new CancellationTokenSource(allowed > TimeSpan.Zero ? allowed : TimeSpan.FromMilliseconds(1), ctx.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCancellation, callTimeout.Token);
        try
        {
            var raw = await p.Function!.InvokeAsync(new AIFunctionArguments(p.Call.Arguments), linked.Token).AsTask().WaitAsync(linked.Token);
            var text = raw as string ?? JsonSerializer.Serialize(raw);
            if (text.Length > ctx.Limits.MaxResultCharacters)
            {
                ctx.Store.Complete(entry, Outcome.Failed, text.Length);
                return new(CallStatus.Failed, $"The result is too large ({text.Length} characters; the limit is {ctx.Limits.MaxResultCharacters}) so it was not returned. Call the tool again with narrower arguments.");
            }

            ctx.Store.Complete(entry, Outcome.Succeeded, text.Length);
            return new(CallStatus.Succeeded, text);
        }
        catch (OperationCanceledException) when (userCancellation.IsCancellationRequested)
        {
            ctx.Store.Complete(entry, Outcome.Cancelled, null);
            throw;
        }
        catch (OperationCanceledException)
        {
            ctx.Store.Complete(entry, Outcome.TimedOut, null);
            if (remaining() <= TimeSpan.Zero) throw new AnswerLimitException("The answer reached the total time limit and was stopped.");
            return new(CallStatus.TimedOut, "The tool did not finish in time and was cancelled.");
        }
        catch (Exception e)
        {
            ctx.Store.Complete(entry, Outcome.Failed, null);
            return new(CallStatus.Failed, "The tool failed unexpectedly: " + e.GetType().Name);
        }
    }
}
