using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

// Throwaway spike (docs/spikes/meai-maf.md). Usage: dotnet run -- <experiment> [model] [adapter]
//   x1    Q1  reasoning / tool-call fragments through IChatClient adapters (scripted server)
//   x2    Q2  every tool-layer requirement through FunctionInvokingChatClient (scripted server)
//   x3    Q3  human confirmation the MEAI/MAF way (stateless resume)
//   x4    Q4  MCP client: our HttpClient, URL guard, redirects, headers, cancellation
//   real  Q1/Q2 against a real Ollama: real <model> <A0|A|B>
//   raw   what Ollama really sends on /v1/chat/completions
//   refl  reflection helper to list the public API of Microsoft.Agents.AI
//   all   x1..x4 (and nothing that needs Ollama)
// SPIKE_NETTRACE=1 prints every DNS lookup / socket connect / HTTP request the process made.

using var trace = NetTrace.StartIfRequested();
var mode = args.Length > 0 ? args[0] : "x1";
var model = args.Length > 1 ? args[1] : "qwen3.8:27b";
var endpoint = new Uri("http://localhost:11434/v1");

switch (mode)
{
    case "x1": await Experiment1.RunAsync(); break;
    case "x2": await Experiment2.RunAllAsync(); break;
    case "x3": await Experiment3.RunAsync(); break;
    case "x4": await Experiment4.RunAsync(); break;
    case "x5": await Experiment5.RunAsync(); break;
    case "x6": await Experiment6.RunAsync(); break;
    case "all":
        await Experiment1.RunAsync();
        await Experiment2.RunAllAsync();
        await Experiment3.RunAsync();
        await Experiment4.RunAsync();
        await Experiment5.RunAsync();
        break;
    case "real":
        await ExperimentReal.RunAsync(model, args.Length > 2 ? args[2] : "B", "What is 1234*5678? And what is today's date? Use the tools.");
        break;
    case "refl": Reflect.Run(args); break;
    case "raw": await RawAsync(); break;
    default: Console.WriteLine("unknown experiment " + mode); break;
}

trace?.Report();
return;

async Task RawAsync()
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var body = $$"""{"model":"{{model}}","stream":true,"messages":[{"role":"user","content":"What is 17*23? Answer in one short sentence."}]}""";
    using var req = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
    var n = 0;
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.Length > 0 && n++ < 8) Console.WriteLine(line);
    }
    Console.WriteLine($"({n} data lines)");
}
