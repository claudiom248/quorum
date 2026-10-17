using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>Q3: human confirmation the MEAI/MAF way (run ends with an approval request, a NEW request resumes it).</summary>
static class Experiment3
{
    static string[] Call(string name, string args, string id = "call_1") =>
        [FakeServer.ToolFragment(new { index = 0, id, type = "function", function = new { name, arguments = args } })];

    static string Describe(IEnumerable<AIContent> contents) => string.Join(", ", contents.Select(c => c switch
    {
        TextContent t => $"Text('{t.Text}')",
        FunctionCallContent f => $"FunctionCall({f.Name})",
        FunctionResultContent r => $"FunctionResult('{r.Result}')",
        ToolApprovalRequestContent a => $"ApprovalRequest(id={a.RequestId[..Math.Min(8, a.RequestId.Length)]}, call={(a.ToolCall as FunctionCallContent)?.Name})",
        ToolApprovalResponseContent a => $"ApprovalResponse(approved={a.Approved})",
        _ => c.GetType().Name,
    }));

    static AIFunction Danger(List<string> executed) => new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string path) => { executed.Add(path); return $"deleted {path}"; }, "srv__danger", "Deletes a path."));
    static AIFunction Calc(List<string> executed) => AIFunctionFactory.Create((string expression) => { executed.Add("calc " + expression); return "2"; }, "native__calculator", "Calculator.");

    public static async Task RunAsync()
    {
        await MeaiOnly();
        await MeaiMixedRound();
        await Maf();
    }

    static async Task MeaiOnly()
    {
        Console.WriteLine("=== 3a MEAI-native approval: FunctionInvokingChatClient + ApprovalRequiredAIFunction, stateless resume");
        await using var server = await FakeServer.StartAsync();
        server.Enqueue(Call("srv__danger", "{\"path\":\"/tmp/x\"}"));
        server.Enqueue([FakeServer.Delta("content", "Deleted.")]);
        var executed = new List<string>();
        var client = new ChatClientBuilder(new QuorumChatClient(new HttpClient(), server.BaseUrl, "fake")).UseFunctionInvocation().Build();
        var options = new ChatOptions { Tools = [Danger(executed), Calc(executed)] };

        // Request 1: the run ends with an approval request.
        List<ChatMessage> history = [new(ChatRole.User, "delete /tmp/x")];
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(history, options)) { updates.Add(u); Console.WriteLine($"  req1 update role={u.Role}: {Describe(u.Contents)}"); }
        Console.WriteLine($"  tool executed after request 1: [{string.Join(",", executed)}]  model requests so far: {server.Requests.Count}");
        var response = updates.ToChatResponse();
        var request = response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();

        // What must be persisted to resume: the assistant message holding the call and the approval request.
        history.AddRange(response.Messages);
        Console.WriteLine("  persisted state = " + JsonSerializer.Serialize(response.Messages, AIJsonUtilities.DefaultOptions).Length + " bytes of JSON (assistant message with call + approval request)");

        // Request 2 (may be a different process): same history + the user's decision.
        var restored = JsonSerializer.Deserialize<List<ChatMessage>>(JsonSerializer.Serialize(history, AIJsonUtilities.DefaultOptions), AIJsonUtilities.DefaultOptions)!;
        restored.Add(new ChatMessage(ChatRole.User, [request.CreateResponse(true)]));
        await foreach (var u in client.GetStreamingResponseAsync(restored, options)) Console.WriteLine($"  req2 update role={u.Role}: {Describe(u.Contents)}");
        Console.WriteLine($"  tool executed after request 2: [{string.Join(",", executed)}]  model requests: {server.Requests.Count}");

        // Denial
        executed.Clear();
        server.Enqueue(Call("srv__danger", "{\"path\":\"/tmp/y\"}"));
        server.Enqueue([FakeServer.Delta("content", "Understood, not deleting.")]);
        var h2 = new List<ChatMessage> { new(ChatRole.User, "delete /tmp/y") };
        var r1 = await client.GetResponseAsync(h2, options);
        h2.AddRange(r1.Messages);
        h2.Add(new ChatMessage(ChatRole.User, [r1.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single().CreateResponse(false, "no")]));
        var r2 = await client.GetResponseAsync(h2, options);
        Console.WriteLine($"  denial: executed=[{string.Join(",", executed)}], answer='{r2.Text}'");
        var last = JsonDocument.Parse(server.Requests[^1]).RootElement.GetProperty("messages").EnumerateArray().Last();
        Console.WriteLine($"  denial: last message sent to the model: role={last.GetProperty("role")} content='{last.GetProperty("content")}'");
    }

    static async Task MeaiMixedRound()
    {
        Console.WriteLine("=== 3b MEAI approval, mixed round: an auto call and a confirm call in the same model turn");
        await using var server = await FakeServer.StartAsync();
        server.Enqueue(
            FakeServer.ToolFragment(new { index = 0, id = "c1", type = "function", function = new { name = "native__calculator", arguments = "{\"expression\":\"1+1\"}" } }),
            FakeServer.ToolFragment(new { index = 1, id = "c2", type = "function", function = new { name = "srv__danger", arguments = "{\"path\":\"/tmp/x\"}" } }),
            FakeServer.ToolFragment(new { index = 2, id = "c3", type = "function", function = new { name = "native__calculator", arguments = "{\"expression\":\"3+3\"}" } }));
        server.Enqueue([FakeServer.Delta("content", "finished")]);
        var executed = new List<string>();
        var client = new ChatClientBuilder(new QuorumChatClient(new HttpClient(), server.BaseUrl, "fake")).UseFunctionInvocation().Build();
        var options = new ChatOptions { Tools = [Danger(executed), Calc(executed)] };
        List<ChatMessage> history = [new(ChatRole.User, "go")];
        var r1 = await client.GetResponseAsync(history, options);
        Console.WriteLine($"  after run 1: executed=[{string.Join(",", executed)}]; response contents: {Describe(r1.Messages.SelectMany(m => m.Contents))}");
        history.AddRange(r1.Messages);
        history.Add(new ChatMessage(ChatRole.User, r1.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Select(a => (AIContent)a.CreateResponse(true)).ToList()));
        var r2 = await client.GetResponseAsync(history, options);
        Console.WriteLine($"  after run 2: executed=[{string.Join(",", executed)}], answer='{r2.Text}' (order of execution above)");
    }

    static async Task Maf()
    {
        Console.WriteLine("=== 3c MAF: ChatClientAgent + AgentSession, approval across two runs and a serialised session");
        await using var server = await FakeServer.StartAsync();
        server.Enqueue(Call("srv__danger", "{\"path\":\"/tmp/x\"}"));
        server.Enqueue([FakeServer.Delta("content", "Deleted.")]);
        var executed = new List<string>();
        IChatClient model = new QuorumChatClient(new HttpClient(), server.BaseUrl, "fake");
        AIAgent agent = model.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "assistant",
            ChatOptions = new ChatOptions { Instructions = "You are helpful.", Tools = [Danger(executed), Calc(executed)] },
        });

        var session = await agent.CreateSessionAsync();
        var updates = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync("delete /tmp/x", session)) { updates.Add(u); Console.WriteLine($"  run1 update: {Describe(u.Contents)}"); }
        var request = updates.SelectMany(u => u.Contents).OfType<ToolApprovalRequestContent>().Single();
        Console.WriteLine($"  executed after run 1: [{string.Join(",", executed)}]");

        // Persist the session, "restart", restore it.
        var json = await agent.SerializeSessionAsync(session);
        Console.WriteLine($"  serialised session: {json.GetRawText().Length} bytes");
        Console.WriteLine("  " + json.GetRawText()[..Math.Min(400, json.GetRawText().Length)].Replace("\n", " "));
        var restored = await agent.DeserializeSessionAsync(json);

        await foreach (var u in agent.RunStreamingAsync(new ChatMessage(ChatRole.User, [request.CreateResponse(true)]), restored)) Console.WriteLine($"  run2 update: {Describe(u.Contents)}");
        Console.WriteLine($"  executed after run 2: [{string.Join(",", executed)}]; model requests: {server.Requests.Count}");
    }
}
