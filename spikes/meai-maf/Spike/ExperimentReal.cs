using System.ClientModel;
using System.Data;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using OpenAI;

/// <summary>The same pipeline as Experiment2, against a real Ollama (reasoning + tool calls + results, streamed).</summary>
static class ExperimentReal
{
    public static async Task RunAsync(string model, string adapter, string prompt)
    {
        var endpoint = new Uri("http://localhost:11434/v1");
        IChatClient inner = adapter switch
        {
            "A" => new ReasoningSurfacingChatClient(new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions { Endpoint = endpoint }).GetChatClient(model).AsIChatClient()),
            "A0" => new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions { Endpoint = endpoint }).GetChatClient(model).AsIChatClient(),
            _ => new QuorumChatClient(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, endpoint, model),
        };

        var store = new FakeStore();
        AIFunction[] tools =
        [
            new PrefixedFunction(AIFunctionFactory.Create((string expression) => new DataTable().Compute(expression, null)?.ToString() ?? "", "calculator", "Evaluates an arithmetic expression such as '12*(3+4)'."), "native__calculator"),
            new PrefixedFunction(AIFunctionFactory.Create(() => DateTimeOffset.Now.ToString("O"), "current_datetime", "Returns the current date and time."), "native__current_datetime"),
        ];
        foreach (var t in tools) store.Tools[t.Name] = Policy.Auto;

        var limits = new ToolLimits(8, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 20_000, TimeSpan.FromMinutes(5));
        var channel = Channel.CreateUnbounded<AnswerEvent>();
        var ctx = new AnswerContext(limits, store, new PendingInteractions(), channel, TimeProvider.System);
        var sw = Stopwatch.StartNew();
        int reasoning = 0, text = 0;
        var answer = new System.Text.StringBuilder();
        Console.WriteLine($"--- {model} via adapter {adapter}");
        await foreach (var e in AnswerRunner.RunAsync(inner, ctx, channel,
                           [new ChatMessage(ChatRole.System, "You are a helpful assistant. Always use the tools for arithmetic and for the date."), new ChatMessage(ChatRole.User, prompt)],
                           new ChatOptions { Tools = [.. tools] }))
        {
            switch (e)
            {
                case ReasoningEvent r: if (reasoning == 0) Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] first reasoning delta"); reasoning += r.Text.Length; break;
                case TextEvent t: if (text == 0) Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] first text delta"); text += t.Text.Length; answer.Append(t.Text); break;
                case ToolCallEvent c: Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] tool_call   {c.Tool} {c.Arguments}"); break;
                case ToolResultEvent r: Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] tool_result {r.Status} {r.Content}"); break;
                case FailedEvent f: Console.WriteLine("FAILED " + f.Message); break;
            }
        }
        Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] done: reasoning={reasoning} chars, text={text} chars");
        Console.WriteLine("ANSWER: " + answer.ToString().Replace("\n", " ")[..Math.Min(300, answer.Length)]);
        Console.WriteLine("audit log: " + string.Join(" | ", store.Log));
    }
}
