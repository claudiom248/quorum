using System.Reflection;
using Microsoft.Extensions.AI;
static class Reflect
{
    public static void Run(string[] args)
    {
        if (args.Length > 1 && args[1] == "where")
        {
            foreach (var t in new[] { typeof(IChatClient), typeof(DelegatingChatClient), typeof(AIFunctionFactory), typeof(DelegatingAIFunction), typeof(ChatClientBuilder), typeof(FunctionInvokingChatClient), typeof(ApprovalRequiredAIFunction), typeof(TextReasoningContent) })
                Console.WriteLine($"{t.Name,-30} {t.Assembly.GetName().Name}");
            return;
        }
        var asm = typeof(Microsoft.Agents.AI.Workflows.Workflow).Assembly;
        var all = asm.GetExportedTypes();
        if (args.Length < 2)
        {
            foreach (var t in all.OrderBy(t => t.FullName))
                Console.WriteLine($"{t.FullName}{(t.GetCustomAttributes().Any(a => a.GetType().Name == "ExperimentalAttribute") ? " [EXPERIMENTAL]" : "")}");
            return;
        }
        foreach (var name in args.Skip(1))
        {
            var t = all.First(t => t.Name == name);
            Console.WriteLine("== " + t.FullName);
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m is not MethodBase { IsSpecialName: true } || m is ConstructorInfo)) Console.WriteLine("  " + m);
        }
    }
}
