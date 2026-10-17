using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;

// Prototype of Quorum's tool layer on top of FunctionInvokingChatClient. Nothing here is production code:
// it exists to prove (or disprove) that every requirement of spec 004 can be reached through MEAI's extension points.

enum Policy { Auto, Confirm }

enum Outcome { Started, Succeeded, Failed, Denied, Cancelled, TimedOut }

sealed record ToolLimits(int MaxRounds, TimeSpan TotalTimeout, TimeSpan CallTimeout, int MaxResultCharacters, TimeSpan ConfirmationTimeout, int UsableCharacters = int.MaxValue);

sealed record AuditEntry(Guid CallId, string Tool, string Arguments, string? Decision)
{
    public Outcome Outcome { get; set; } = Outcome.Started;
    public int? ResultCharacters { get; set; }
}

/// <summary>Stands for the database: tool state/policy at call time, and the audit log (can be made to fail).</summary>
sealed class FakeStore
{
    public Dictionary<string, Policy?> Tools { get; } = [];
    public List<AuditEntry> Audit { get; } = [];
    public List<string> Log { get; } = [];
    public bool FailAuditWrites { get; set; }

    public ValueTask<Policy?> CurrentAsync(string name) => ValueTask.FromResult(Tools.GetValueOrDefault(name));

    public Task StartAsync(AuditEntry entry)
    {
        if (FailAuditWrites) throw new InvalidOperationException("audit store down");
        Audit.Add(entry);
        Log.Add($"audit:start:{entry.Tool}");
        return Task.CompletedTask;
    }

    public void Complete(AuditEntry entry, Outcome outcome, int? size)
    {
        entry.Outcome = outcome;
        entry.ResultCharacters = size;
        Log.Add($"audit:{outcome}:{entry.Tool}");
    }
}

/// <summary>The in-memory registry of pending confirmations of design A (stream stays open).</summary>
sealed class PendingInteractions
{
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _pending = [];

    public Task<bool> RegisterAsync(Guid callId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[callId] = tcs;
        return tcs.Task.WaitAsync(timeout, ct);
    }

    public bool Decide(Guid callId, bool approve)
    {
        lock (_pending) return _pending.TryGetValue(callId, out var tcs) && tcs.TrySetResult(approve);
    }
}

abstract record AnswerEvent;
sealed record TextEvent(string Text) : AnswerEvent;
sealed record ReasoningEvent(string Text) : AnswerEvent;
sealed record ToolCallEvent(Guid CallId, string Tool, string Arguments, bool RequiresConfirmation, DateTimeOffset? ExpiresAt) : AnswerEvent;
sealed record ToolResultEvent(Guid CallId, string Status, string Content, int Size) : AnswerEvent;
sealed record FailedEvent(string Message) : AnswerEvent;

/// <summary>A tool exposed with its prefixed name (D-16) over any AIFunction (native code or, later, McpClientTool).</summary>
sealed class PrefixedFunction(AIFunction inner, string exposedName) : DelegatingAIFunction(inner)
{
    public override string Name => exposedName;
}

sealed class AnswerContext(ToolLimits limits, FakeStore store, PendingInteractions pending, Channel<AnswerEvent> events, TimeProvider clock)
{
    public ToolLimits Limits => limits;
    public FakeStore Store => store;
    public PendingInteractions Pending => pending;
    public ChannelWriter<AnswerEvent> Events => events.Writer;
    public TimeProvider Clock => clock;
    public CancellationToken UserCancellation { get; set; }
}

static class QuorumInvoker
{
    /// <summary>
    /// The FunctionInvoker of FunctionInvokingChatClient. It re-implements what ToolExecutor does today: call-time checks,
    /// argument validation, confirmation, audit-before-run (fail closed), call timeout, result cap, audit completion.
    /// </summary>
    public static Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> Create(AnswerContext ctx) => async (invocation, ct) =>
    {
        var name = invocation.Function.Name;
        var callId = Guid.CreateVersion7();
        var arguments = JsonSerializer.Serialize(invocation.Arguments);
        var entry = new AuditEntry(callId, name, arguments, null);

        async Task<object?> Refuse(string status, string message, string code)
        {
            await ctx.Events.WriteAsync(new ToolResultEvent(callId, status, message, message.Length));
            ctx.Store.Log.Add($"refused:{code}:{name}");
            return message;
        }

        // 1. state and policy at call time
        var policy = await ctx.Store.CurrentAsync(name);
        if (policy is null)
        {
            await ctx.Events.WriteAsync(new ToolCallEvent(callId, name, arguments, false, null));
            return await Refuse("failed", $"The tool '{name}' is no longer available.", "tool.unavailable");
        }

        // 2. schema validation (a real implementation would reuse ToolArgumentValidator)
        var problem = Validate(invocation);
        if (problem is not null)
        {
            await ctx.Events.WriteAsync(new ToolCallEvent(callId, name, arguments, false, null));
            return await Refuse("failed", $"The arguments for '{name}' are not valid. {problem}", "tool.invalid_arguments");
        }

        // 3. confirmation (design A: wait inside the request)
        string decision = "Auto";
        if (policy == Policy.Confirm)
        {
            var expires = ctx.Clock.GetUtcNow() + ctx.Limits.ConfirmationTimeout;
            await ctx.Events.WriteAsync(new ToolCallEvent(callId, name, arguments, true, expires));
            var wait = ctx.Pending.RegisterAsync(callId, ctx.Limits.ConfirmationTimeout, ct);
            try
            {
                decision = await wait ? "Approved" : "Denied";
            }
            catch (TimeoutException)
            {
                decision = "TimedOut";
            }

            if (decision != "Approved")
            {
                entry = entry with { Decision = decision };
                await ctx.Store.StartAsync(entry);
                return await Refuse("denied", decision == "Denied" ? "The user denied the call." : "The user did not answer in time.", "tool.denied");
            }
        }
        else
        {
            await ctx.Events.WriteAsync(new ToolCallEvent(callId, name, arguments, false, null));
        }

        // 4. audit before running: fail closed (D-28)
        try
        {
            entry = entry with { Decision = decision };
                await ctx.Store.StartAsync(entry);
        }
        catch (Exception)
        {
            return await Refuse("failed", "The tool was not run because the call could not be recorded in the audit log.", "audit.failed");
        }

        // 5. run with the call timeout
        using var callTimeout = new CancellationTokenSource(ctx.Limits.CallTimeout, ctx.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, callTimeout.Token);
        string status; string content; Outcome outcome; int? size = null;
        try
        {
            var raw = await invocation.Function.InvokeAsync(invocation.Arguments, linked.Token).AsTask().WaitAsync(linked.Token);
            var text = raw as string ?? JsonSerializer.Serialize(raw);
            size = text.Length;
            if (text.Length > ctx.Limits.MaxResultCharacters)
            {
                (status, outcome) = ("failed", Outcome.Failed);
                content = $"The result is too large ({text.Length} characters; the limit is {ctx.Limits.MaxResultCharacters}) so it was not returned. Call the tool again with narrower arguments.";
            }
            else
            {
                (status, outcome, content) = ("succeeded", Outcome.Succeeded, text);
            }
        }
        catch (OperationCanceledException) when (ctx.UserCancellation.IsCancellationRequested)
        {
            ctx.Store.Complete(entry, Outcome.Cancelled, null);
            throw;
        }
        catch (OperationCanceledException)
        {
            (status, outcome) = ("timed_out", Outcome.TimedOut);
            content = callTimeout.IsCancellationRequested
                ? $"The tool did not finish in time and was cancelled after {ctx.Limits.CallTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds."
                : "The answer reached its total time limit and the tool was cancelled.";
        }
        catch (Exception exception)
        {
            (status, outcome) = ("failed", Outcome.Failed);
            content = "The tool failed unexpectedly: " + exception.GetType().Name;
        }

        ctx.Store.Complete(entry, outcome, size);
        await ctx.Events.WriteAsync(new ToolResultEvent(callId, status, content, content.Length));
        return content;
    };

    private static string? Validate(FunctionInvocationContext invocation)
    {
        // Minimal required-properties check against the function's own JSON schema.
        var schema = invocation.Function.JsonSchema;
        if (schema.TryGetProperty("required", out var required))
            foreach (var r in required.EnumerateArray())
                if (!invocation.Arguments.ContainsKey(r.GetString()!))
                    return $"Missing required argument '{r.GetString()}'.";
        return null;
    }
}

/// <summary>Sits between FunctionInvokingChatClient and the model: sees the full request of every round (context budget accounting).</summary>
sealed class BudgetChatClient(IChatClient inner, int usableCharacters) : DelegatingChatClient(inner)
{
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var total = list.Sum(m => m.Contents.Sum(c => c switch
        {
            TextContent t => t.Text.Length,
            FunctionResultContent r => r.Result?.ToString()?.Length ?? 0,
            FunctionCallContent f => JsonSerializer.Serialize(f.Arguments).Length + f.Name.Length,
            _ => 0,
        }));
        if (total > usableCharacters)
            throw new InvalidOperationException("The tool results filled the context of the model, so the answer was stopped.");
        return base.GetStreamingResponseAsync(list, options, cancellationToken);
    }
}

static class AnswerRunner
{
    public static IChatClient Build(IChatClient model, AnswerContext ctx) =>
        new ChatClientBuilder(model)
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = ctx.Limits.MaxRounds;
                f.AllowConcurrentInvocation = false;
                f.TerminateOnUnknownCalls = false;
                f.IncludeDetailedErrors = true;
                f.FunctionInvoker = QuorumInvoker.Create(ctx);
            })
            .Use(next => new BudgetChatClient(next, ctx.Limits.UsableCharacters))
            .Build();

    /// <summary>
    /// Runs the model + tool loop on a background task and merges its updates with the events the invoker emits (needed so that
    /// a tool_call event reaches the client WHILE the invoker is waiting for a confirmation).
    /// </summary>
    public static async IAsyncEnumerable<AnswerEvent> RunAsync(
        IChatClient model, AnswerContext ctx, Channel<AnswerEvent> channel, IList<ChatMessage> messages, ChatOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken userCancellation = default)
    {
        using var totalLimit = new CancellationTokenSource(ctx.Limits.TotalTimeout, ctx.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCancellation, totalLimit.Token);
        ctx.UserCancellation = userCancellation;
        var client = Build(model, ctx);

        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in client.GetStreamingResponseAsync(messages, options, linked.Token))
                    foreach (var content in update.Contents)
                    {
                        if (content is TextReasoningContent r) await channel.Writer.WriteAsync(new ReasoningEvent(r.Text));
                        else if (content is TextContent t && t.Text.Length > 0) await channel.Writer.WriteAsync(new TextEvent(t.Text));
                    }
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (totalLimit.IsCancellationRequested && !userCancellation.IsCancellationRequested)
            {
                await channel.Writer.WriteAsync(new FailedEvent("total time limit reached"));
                channel.Writer.TryComplete();
            }
            catch (Exception e)
            {
                channel.Writer.TryComplete(e);
            }
        }, CancellationToken.None);

        await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None)) yield return e;
        await pump;
    }
}
