using System.Data;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>Option "own loop as IChatClient middleware": same scenarios as Experiment2, no FunctionInvokingChatClient, no pump.</summary>
static class Experiment6
{
    static ToolLimits Defaults => new(8, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 20_000, TimeSpan.FromMinutes(5));

    static AIFunction[] Tools() =>
    [
        new PrefixedFunction(AIFunctionFactory.Create((string expression) => new DataTable().Compute(expression, null)?.ToString() ?? "", "calculator", "Evaluates an arithmetic expression."), "native__calculator"),
        new PrefixedFunction(AIFunctionFactory.Create(async (int ms, CancellationToken ct) => { await Task.Delay(ms, ct); return "done"; }, "slow", "Waits."), "native__slow"),
        new PrefixedFunction(AIFunctionFactory.Create((int n) => new string('x', n), "big", "Big output."), "native__big"),
        new PrefixedFunction(AIFunctionFactory.Create(async (int ms) => { await Task.Delay(ms); return "ignored-token"; }, "deaf", "Never looks at its token."), "native__deaf"),
        new PrefixedFunction(AIFunctionFactory.Create((string path) => $"deleted {path}", "danger", "Deletes."), "srv__danger"),
    ];

    static string[] Call(string name, string args, string id = "call_1") =>
        [FakeServer.ToolFragment(new { index = 0, id, type = "function", function = new { name, arguments = args } })];

    static string[] Text(string t) => [FakeServer.Delta("content", t)];

    static async Task Run(string title, string[][] rounds, ToolLimits limits, Action<FakeStore>? setup = null,
        Func<Guid, PendingInteractions, Task>? onConfirm = null, CancellationTokenSource? cancel = null)
    {
        await using var server = await FakeServer.StartAsync();
        foreach (var r in rounds) server.Enqueue(r);
        var store = new FakeStore();
        foreach (var t in Tools()) store.Tools[t.Name] = t.Name.StartsWith("srv__") ? Policy.Confirm : Policy.Auto;
        setup?.Invoke(store);
        var pending = new PendingInteractions();
        var ctx = new AnswerContext(limits, store, pending, Channel.CreateUnbounded<AnswerEvent>(), TimeProvider.System);
        IChatClient client = new ToolLoopChatClient(new QuorumChatClient(new HttpClient(), server.BaseUrl, "fake"), ctx);

        Console.WriteLine($"=== {title}");
        string? error = null;
        try
        {
            await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [.. Tools()] }, cancel?.Token ?? default))
                foreach (var c in u.Contents)
                {
                    switch (c)
                    {
                        case TextContent t: Console.WriteLine($"   sse> text '{t.Text}'"); break;
                        case TextReasoningContent r: Console.WriteLine($"   sse> reasoning '{r.Text}'"); break;
                        case FunctionCallContent f:
                            var confirm = (bool)c.AdditionalProperties![ToolLoopChatClient.RequiresConfirmationKey]!;
                            Console.WriteLine($"   sse> tool_call {f.Name} confirm={confirm}");
                            if (confirm && onConfirm is not null) { var id = (Guid)c.AdditionalProperties![ToolLoopChatClient.CallIdKey]!; _ = Task.Run(() => onConfirm(id, pending)); }
                            break;
                        case FunctionResultContent fr: Console.WriteLine($"   sse> tool_result {c.AdditionalProperties![ToolLoopChatClient.StatusKey]} '{Short(fr.Result?.ToString() ?? "")}'"); break;
                    }
                }
        }
        catch (Exception e) { error = $"{e.GetType().Name}: {e.Message}"; }
        if (error is not null) Console.WriteLine("   ENDED> " + error);
        Console.WriteLine("   log> " + string.Join(" | ", store.Log) + $"   (model requests: {server.Requests.Count})");
    }

    static string Short(string s) => s.Length <= 80 ? s : s[..80] + "…";

    public static async Task RunAsync()
    {
        await Run("U1 happy path", [Call("native__calculator", "{\"expression\":\"6*7\"}"), Text("It is 42.")], Defaults);
        await Run("U2 unknown tool: now audited and with our own message", [Call("native__nope", "{}"), Text("sorry")], Defaults);
        await Run("U4 audit fails -> not run", [Call("native__calculator", "{\"expression\":\"1+1\"}"), Text("ok")], Defaults, s => s.FailAuditWrites = true);
        await Run("U5 call timeout (tool ignores its token)", [Call("native__deaf", "{\"ms\":3000}"), Text("ok")], Defaults with { CallTimeout = TimeSpan.FromMilliseconds(300) });
        await Run("U6 total time limit (400 ms tools, 700 ms total)",
            [Call("native__slow", "{\"ms\":400}", "a"), Call("native__slow", "{\"ms\":400}", "b"), Call("native__slow", "{\"ms\":400}", "c"), Text("done")], Defaults with { TotalTimeout = TimeSpan.FromMilliseconds(700) });
        await Run("U7 rounds: model asks forever, MaxRounds=3 (exactly 3 model calls, last calls NOT run)",
            Enumerable.Range(0, 10).Select(i => Call("native__calculator", "{\"expression\":\"1+1\"}", "c" + i)).ToArray(), Defaults with { MaxRounds = 3 });
        await Run("U8 result over the cap -> error, no truncation", [Call("native__big", "{\"n\":500}"), Text("ok")], Defaults with { MaxResultCharacters = 100 });
        using (var cancel = new CancellationTokenSource())
            await Run("U10 user stops during a tool", [Call("native__slow", "{\"ms\":5000}"), Text("ok")], Defaults, cancel: cancel.Also(c => c.CancelAfter(400)));
        await Run("U11a confirm approved", [Call("srv__danger", "{\"path\":\"/tmp/x\"}"), Text("deleted")], Defaults, onConfirm: (id, p) => { p.Decide(id, true); return Task.CompletedTask; });
        await Run("U11b confirm denied", [Call("srv__danger", "{\"path\":\"/tmp/x\"}"), Text("ok")], Defaults, onConfirm: (id, p) => { p.Decide(id, false); return Task.CompletedTask; });
        await Run("U11c confirm timeout", [Call("srv__danger", "{\"path\":\"/tmp/x\"}"), Text("ok")], Defaults with { ConfirmationTimeout = TimeSpan.FromMilliseconds(300) });
        await Run("U14 confirmation wait is NOT counted in the total time (total 500 ms, user answers after 900 ms)",
            [Call("srv__danger", "{\"path\":\"/tmp/x\"}"), Text("deleted")], Defaults with { TotalTimeout = TimeSpan.FromMilliseconds(500) },
            onConfirm: async (id, p) => { await Task.Delay(900); p.Decide(id, true); });
        await Run("U12 context budget", [Call("native__big", "{\"n\":5000}"), Text("ok")], Defaults with { UsableCharacters = 1000 });

        await InsideMafAgent();
    }

    /// <summary>The same middleware supplied to a MAF agent as-is: the agent gets our loop, policies and audit.</summary>
    static async Task InsideMafAgent()
    {
        Console.WriteLine("=== U20 the loop middleware inside a MAF ChatClientAgent (UseProvidedChatClientAsIs)");
        await using var server = await FakeServer.StartAsync();
        server.Enqueue(Call("native__calculator", "{\"expression\":\"6*7\"}"));
        server.Enqueue(Text("It is 42."));
        var store = new FakeStore();
        foreach (var t in Tools()) store.Tools[t.Name] = Policy.Auto;
        var ctx = new AnswerContext(Defaults, store, new PendingInteractions(), Channel.CreateUnbounded<AnswerEvent>(), TimeProvider.System);
        IChatClient client = new ToolLoopChatClient(new QuorumChatClient(new HttpClient(), server.BaseUrl, "fake"), ctx);
        AIAgent agent = client.AsAIAgent(new ChatClientAgentOptions { Id = "a", Name = "assistant", UseProvidedChatClientAsIs = true, ChatOptions = new ChatOptions { Tools = [.. Tools()] } });
        await foreach (var u in agent.RunStreamingAsync("go"))
            foreach (var c in u.Contents)
                Console.WriteLine($"   agent update> {c.GetType().Name} {(c is TextContent t ? t.Text : c is FunctionCallContent f ? f.Name : c is FunctionResultContent r ? r.Result : "")} props=[{string.Join(",", c.AdditionalProperties?.Keys ?? [])}]");
        Console.WriteLine("   log> " + string.Join(" | ", store.Log));
    }

    static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
