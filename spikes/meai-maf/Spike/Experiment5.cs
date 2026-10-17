using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

/// <summary>Q5: what MAF workflows give for multi-agent topologies, and whether our tool layer (policy/audit/limits) still applies inside them.</summary>
static class Experiment5
{
    public static async Task RunAsync()
    {
        await using var server = await FakeServer.StartAsync();
        var store = new FakeStore();
        store.Tools["native__calculator"] = Policy.Auto;
        var channel = Channel.CreateUnbounded<AnswerEvent>();
        var ctx = new AnswerContext(new ToolLimits(8, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), 20_000, TimeSpan.FromMinutes(5)), store, new PendingInteractions(), channel, TimeProvider.System);
        var calculator = new PrefixedFunction(AIFunctionFactory.Create((string expression) => "42", "calculator", "Calculator."), "native__calculator");

        // Agent "researcher": our FICC pipeline (FunctionInvoker = QuorumInvoker) supplied as-is; agents "critic" and "writer": plain.
        IChatClient Model() => new QuorumChatClient(new HttpClient(), server.BaseUrl, "fake");
        var researcherClient = new ChatClientBuilder(Model())
            .UseFunctionInvocation(configure: f => { f.FunctionInvoker = QuorumInvoker.Create(ctx); f.AllowConcurrentInvocation = false; })
            .Build();

        AIAgent researcher = researcherClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "researcher", Name = "researcher", UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions { Instructions = "You research.", Tools = [calculator] },
        });
        AIAgent critic = Model().AsAIAgent(new ChatClientAgentOptions { Id = "critic", Name = "critic", ChatOptions = new ChatOptions { Instructions = "You criticise." } });
        AIAgent writer = Model().AsAIAgent(new ChatClientAgentOptions { Id = "writer", Name = "writer", ChatOptions = new ChatOptions { Instructions = "You write the final answer." } });

        Console.WriteLine("=== 5a sequential orchestration researcher -> critic -> writer (researcher calls a tool through OUR invoker)");
        server.Enqueue([FakeServer.ToolFragment(new { index = 0, id = "c1", type = "function", function = new { name = "native__calculator", arguments = "{\"expression\":\"6*7\"}" } })]);
        server.Enqueue([FakeServer.Delta("content", "Research: the answer is 42.")]);
        server.Enqueue([FakeServer.Delta("content", "Critique: plausible.")]);
        server.Enqueue([FakeServer.Delta("content", "Final: 42.")]);
        var workflow = AgentWorkflowBuilder.BuildSequential([researcher, critic, writer]);
        await Watch(workflow, "go");
        Console.WriteLine("   our audit log (policy/audit ran inside the workflow): " + string.Join(" | ", store.Log));
        Console.WriteLine($"   model requests: {server.Requests.Count}");

        Console.WriteLine("=== 5b concurrent orchestration (fan-out to 3 agents, aggregated)");
        for (var i = 0; i < 3; i++) server.Enqueue([FakeServer.Delta("content", $"answer-{i}")]);
        var concurrent = AgentWorkflowBuilder.BuildConcurrent([researcher.WithName("a"), critic.WithName("b"), writer.WithName("c")]);
        await Watch(concurrent, "go");
    }

    static async Task Watch(Workflow workflow, string input)
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, new List<ChatMessage> { new(ChatRole.User, input) });
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            switch (evt)
            {
                case AgentResponseUpdateEvent u when u.Update.Contents.OfType<TextContent>().Any():
                    Console.WriteLine($"   stream> [{u.ExecutorId}] {string.Concat(u.Update.Contents.OfType<TextContent>().Select(t => t.Text))}");
                    break;
                case WorkflowOutputEvent o:
                    Console.WriteLine($"   output> {o.Data?.GetType().Name}");
                    break;
                case ExecutorFailedEvent f:
                    Console.WriteLine($"   FAILED> {f.ExecutorId}: {f.Data}");
                    break;
            }
        }
    }

    static AIAgent WithName(this AIAgent agent, string name) => agent;
}
