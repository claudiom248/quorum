using System.Data;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;

/// <summary>Q2: every requirement of the tool layer through FunctionInvokingChatClient's extension points, against a scripted server.</summary>
static class Experiment2
{
    static ToolLimits Defaults => new(8, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 20_000, TimeSpan.FromMinutes(5));

    static AIFunction[] Tools() =>
    [
        new PrefixedFunction(AIFunctionFactory.Create((string expression) => new DataTable().Compute(expression, null)?.ToString() ?? "", "calculator", "Evaluates an arithmetic expression."), "native__calculator"),
        new PrefixedFunction(AIFunctionFactory.Create(() => "2026-10-17T12:00:00+02:00", "current_datetime", "Current date and time."), "native__current_datetime"),
        new PrefixedFunction(AIFunctionFactory.Create(async (int ms, CancellationToken ct) => { await Task.Delay(ms, ct); return "done"; }, "slow", "Waits."), "native__slow"),
        new PrefixedFunction(AIFunctionFactory.Create((int n) => new string('x', n), "big", "Big output."), "native__big"),
        new PrefixedFunction(AIFunctionFactory.Create(async (int ms) => { await Task.Delay(ms); return "ignored-token"; }, "deaf", "Never looks at its token."), "native__deaf"),
        new PrefixedFunction(AIFunctionFactory.Create((string path) => $"deleted {path}", "danger", "Deletes."), "srv__danger"),
    ];

    static string[] Call(string name, string args, string id = "call_1") =>
        [FakeServer.ToolFragment(new { index = 0, id, type = "function", function = new { name, arguments = args } })];

    static string[] Calls(params (string Name, string Args, string Id)[] calls) =>
        calls.Select((c, i) => FakeServer.ToolFragment(new { index = i, id = c.Id, type = "function", function = new { name = c.Name, arguments = c.Args } })).ToArray();

    static string[] Text(string t) => [FakeServer.Delta("content", t)];

    sealed record Result(List<string> Events, FakeStore Store, FakeServer Server, string? Error);

    static async Task<Result> RunAsync(string[][] rounds, ToolLimits limits, Action<FakeStore, PendingInteractions, List<AnswerEvent>>? setup = null,
        Func<Guid, PendingInteractions, Task>? onCall = null, CancellationTokenSource? userCancel = null, bool userMessageOnly = true, TimeProvider? clock = null)
    {
        await using var server = await FakeServer.StartAsync();
        foreach (var r in rounds) server.Enqueue(r);
        var store = new FakeStore();
        foreach (var t in Tools()) store.Tools[t.Name] = t.Name.StartsWith("srv__") ? Policy.Confirm : Policy.Auto;
        var pending = new PendingInteractions();
        var channel = Channel.CreateUnbounded<AnswerEvent>();
        var ctx = new AnswerContext(limits, store, pending, channel, clock ?? TimeProvider.System);
        setup?.Invoke(store, pending, []);

        var model = new QuorumChatClient(new HttpClient(), server.BaseUrl, "fake");
        var options = new ChatOptions { Tools = [.. Tools()] };
        var events = new List<string>();
        string? error = null;
        try
        {
            await foreach (var e in AnswerRunner.RunAsync(model, ctx, channel, [new ChatMessage(ChatRole.User, "go")], options, userCancel?.Token ?? default))
            {
                events.Add(e switch
                {
                    TextEvent t => $"text '{t.Text}'",
                    ReasoningEvent r => $"reasoning '{r.Text}'",
                    ToolCallEvent c => $"tool_call {c.Tool} args={c.Arguments} confirm={c.RequiresConfirmation}",
                    ToolResultEvent r => $"tool_result {r.Status} '{Short(r.Content)}'",
                    FailedEvent f => $"FAILED {f.Message}",
                    _ => e.ToString()!,
                });
                if (e is ToolCallEvent { RequiresConfirmation: true } cc && onCall is not null) _ = Task.Run(() => onCall(cc.CallId, pending));
            }
        }
        catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; }
        return new Result(events, store, server, error);
    }

    static string Short(string s) => s.Length <= 90 ? s : s[..90] + "…";

    static void Print(string title, Result r, bool showToolMessages = false)
    {
        Console.WriteLine($"=== {title}");
        foreach (var e in r.Events) Console.WriteLine("   sse> " + e);
        if (r.Error is not null) Console.WriteLine("   EXCEPTION> " + r.Error);
        Console.WriteLine("   log> " + string.Join(" | ", r.Store.Log));
        Console.WriteLine("   audit> " + string.Join(" | ", r.Store.Audit.Select(a => $"{a.Tool}:{a.Outcome}:{a.Decision}")));
        Console.WriteLine($"   model requests: {r.Server.Requests.Count}");
        if (showToolMessages)
            for (var i = 0; i < r.Server.Requests.Count; i++)
            {
                using var doc = JsonDocument.Parse(r.Server.Requests[i]);
                var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).ToArray();
                var names = doc.RootElement.TryGetProperty("tools", out var t) ? string.Join(",", t.EnumerateArray().Select(x => x.GetProperty("function").GetProperty("name").GetString())) : "(no tools)";
                var toolMsgs = doc.RootElement.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").Select(m => Short(m.GetProperty("content").GetString() ?? "")).ToArray();
                Console.WriteLine($"   req#{i + 1}: roles=[{string.Join(",", msgs)}] tools=[{names}] toolMessages=[{string.Join(" || ", toolMsgs)}]");
            }
    }

    public static async Task RunAllAsync()
    {
        Print("T1 happy path (prefixed names, audit before run, tool_call/tool_result events)",
            await RunAsync([Call("native__calculator", "{\"expression\":\"6*7\"}"), Text("It is 42.")], Defaults), true);

        Print("T2 unknown tool", await RunAsync([Call("native__nope", "{}"), Text("sorry")], Defaults), true);

        Print("T3 invalid arguments (missing required)", await RunAsync([Call("native__calculator", "{}"), Text("ok")], Defaults), true);

        Print("T3b arguments of the wrong type (string for int)", await RunAsync([Call("native__big", "{\"n\":\"abc\"}"), Text("ok")], Defaults), true);

        Print("T4 audit write fails -> call must not run (fail closed)",
            await RunAsync([Call("native__calculator", "{\"expression\":\"1+1\"}"), Text("ok")], Defaults, (s, _, _) => s.FailAuditWrites = true), true);

        Print("T5 per-call timeout (slow tool honours token)",
            await RunAsync([Call("native__slow", "{\"ms\":3000}"), Text("ok")], Defaults with { CallTimeout = TimeSpan.FromMilliseconds(300) }), true);

        Print("T5b per-call timeout (tool ignores token)",
            await RunAsync([Call("native__deaf", "{\"ms\":3000}"), Text("ok")], Defaults with { CallTimeout = TimeSpan.FromMilliseconds(300) }), true);

        Print("T6 total time limit (3 rounds of 400 ms tools, total 700 ms)",
            await RunAsync([Call("native__slow", "{\"ms\":400}", "a"), Call("native__slow", "{\"ms\":400}", "b"), Call("native__slow", "{\"ms\":400}", "c"), Text("done")],
                Defaults with { TotalTimeout = TimeSpan.FromMilliseconds(700) }), true);

        Print("T7 round limit: model asks for a tool forever, MaxRounds=3",
            await RunAsync(Enumerable.Range(0, 10).Select(i => Call("native__calculator", "{\"expression\":\"1+1\"}", "c" + i)).ToArray(), Defaults with { MaxRounds = 3 }), true);

        Print("T8 result too large (cap 100, tool returns 500)",
            await RunAsync([Call("native__big", "{\"n\":500}"), Text("ok")], Defaults with { MaxResultCharacters = 100 }), true);

        Print("T9 two calls in one round with different delays: sequential, in order",
            await RunAsync([Calls(("native__slow", "{\"ms\":300}", "a"), ("native__calculator", "{\"expression\":\"1+1\"}", "b")), Text("ok")], Defaults), true);

        using (var cancel = new CancellationTokenSource())
        {
            Print("T10 user stops the answer during a tool call",
                await RunAsync([Call("native__slow", "{\"ms\":5000}"), Text("ok")], Defaults, onCall: null, userCancel: cancel.Also(c => c.CancelAfter(400))), true);
        }

        Print("T11a confirm: approved",
            await RunAsync([Call("srv__danger", "{\"path\":\"/tmp/x\"}"), Text("deleted")], Defaults, onCall: (id, p) => { p.Decide(id, true); return Task.CompletedTask; }), true);
        Print("T11b confirm: denied",
            await RunAsync([Call("srv__danger", "{\"path\":\"/tmp/x\"}"), Text("ok")], Defaults, onCall: (id, p) => { p.Decide(id, false); return Task.CompletedTask; }), true);
        Print("T11c confirm: timeout (300 ms)",
            await RunAsync([Call("srv__danger", "{\"path\":\"/tmp/x\"}"), Text("ok")], Defaults with { ConfirmationTimeout = TimeSpan.FromMilliseconds(300) }), true);

        Print("T12 context budget: result fills the context",
            await RunAsync([Call("native__big", "{\"n\":5000}"), Text("ok")], Defaults with { UsableCharacters = 1000 }), true);

        Print("T13 tool disabled between request and call",
            await RunAsync([Call("native__calculator", "{\"expression\":\"1+1\"}"), Text("ok")], Defaults, (s, _, _) => s.Tools["native__calculator"] = null), true);
    }

    static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
