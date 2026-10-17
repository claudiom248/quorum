using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

/// <summary>Q1: does an IChatClient surface reasoning deltas and tool-call fragments correctly? Scripted server, adapters A0/A/B.</summary>
static class Experiment1
{
    static (string Name, string[] Round)[] Scenarios() =>
    [
        ("litellm reasoning_content", [FakeServer.Delta("reasoning_content", "think-1 "), FakeServer.Delta("reasoning_content", "think-2"), FakeServer.Delta("content", "answer")]),
        ("ollama reasoning", [FakeServer.Delta("reasoning", "r1 "), FakeServer.Delta("reasoning", "r2"), FakeServer.Delta("content", "answer")]),
        ("openai fragments by index", [
            FakeServer.ToolFragment(new { index = 0, id = "call_a", type = "function", function = new { name = "calc", arguments = "" } }),
            FakeServer.ToolFragment(new { index = 0, function = new { arguments = "{\"expr" } }),
            FakeServer.ToolFragment(new { index = 0, function = new { arguments = "ession\":\"1+1\"}" } }),
            FakeServer.ToolFragment(new { index = 1, id = "call_b", type = "function", function = new { name = "now", arguments = "{}" } })]),
        ("ollama whole call, string args", [FakeServer.ToolFragment(new { index = 0, id = "call_x", type = "function", function = new { name = "calc", arguments = "{\"expression\":\"2*3\"}" } })]),
        ("whole call, args as OBJECT, no id", [FakeServer.ToolFragment(new { index = 0, type = "function", function = new { name = "calc", arguments = new { expression = "2*3" } } })]),
        ("two parallel calls both index 0 (distinct ids)", [
            FakeServer.ToolFragment(new { index = 0, id = "call_p", type = "function", function = new { name = "calc", arguments = "{\"expression\":\"1\"}" } }),
            FakeServer.ToolFragment(new { index = 0, id = "call_q", type = "function", function = new { name = "now", arguments = "{}" } })]),
    ];

    public static async Task RunAsync()
    {
        await using var server = await FakeServer.StartAsync();
        foreach (var (name, round) in Scenarios())
        {
            Console.WriteLine($"--- {name}");
            foreach (var (label, client) in new (string, IChatClient)[]
            {
                ("A0 MEAI.OpenAI plain ", new OpenAIClient(new ApiKeyCredential("x"), new OpenAIClientOptions { Endpoint = server.BaseUrl }).GetChatClient("fake").AsIChatClient()),
                ("A  MEAI.OpenAI+patch  ", new ReasoningSurfacingChatClient(new OpenAIClient(new ApiKeyCredential("x"), new OpenAIClientOptions { Endpoint = server.BaseUrl }).GetChatClient("fake").AsIChatClient())),
                ("B  own IChatClient    ", new QuorumChatClient(new HttpClient(), server.BaseUrl, "fake")),
            })
            {
                server.Enqueue(round);
                var options = new ChatOptions { Tools = [AIFunctionFactory.Create((string expression) => "x", "calc"), AIFunctionFactory.Create(() => "x", "now")] };
                try
                {
                    var reasoning = ""; var text = ""; var calls = new List<string>();
                    await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
                        foreach (var c in u.Contents)
                            switch (c)
                            {
                                case TextReasoningContent r: reasoning += r.Text; break;
                                case TextContent t: text += t.Text; break;
                                case FunctionCallContent f: calls.Add($"{f.Name}#{(f.CallId.Length > 12 ? "gen" : f.CallId)}({System.Text.Json.JsonSerializer.Serialize(f.Arguments)})"); break;
                            }
                    Console.WriteLine($"  {label}: reasoning='{reasoning}' text='{text}' calls=[{string.Join("; ", calls)}]");
                }
                catch (Exception e) { Console.WriteLine($"  {label}: EXCEPTION {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
            }
        }
    }
}
