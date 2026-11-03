using System.Text.Json;
using Quorum.Modules.Agents.Domain;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// A sub-agent as a turn runs it, fixed when the turn starts (spec 013 AC-25): the child's prompt, its model, and the tools it owns.
/// </summary>
/// <param name="Instructions">The system prompt, with the notices about tool results when the sub-agent has tools.</param>
/// <param name="ApiKey">Already revealed; the sub-agent calls its model like the root agent does.</param>
/// <param name="Tools">Its own tools; never delegation tools (D-5) and never <c>ask_user</c> (D-40).</param>
/// <param name="ToolsCharacters">What the definitions of <paramref name="Tools"/> take in its requests.</param>
internal sealed record SubAgentDefinition(
    Guid AgentId,
    string Slug,
    string Description,
    SubAgentFailurePolicy FailurePolicy,
    Uri BaseUrl,
    string ModelName,
    string? ApiKey,
    int ContextTokenBudget,
    string Instructions,
    IReadOnlyList<QuorumTool> Tools,
    int ToolsCharacters);

/// <summary>
/// The tool through which an agent calls a sub-agent (spec 013 D-3): named by the child's slug, described by the child's description,
/// with one argument, the task. It runs nothing itself: <see cref="ToolCallInvoker"/> hands the call to <c>SubAgentRunner</c>,
/// because only the invoker has the answer's events, gates and clock.
/// </summary>
internal sealed class AgentQuorumTool : QuorumTool
{
    public const string TaskArgument = "task";

    private const string Schema = """
        {"type":"object","properties":{"task":{"type":"string","description":"What the agent must do, with all the context it needs: it sees nothing of the conversation."}},"required":["task"]}
        """;

    public AgentQuorumTool(SubAgentDefinition agent)
        : base(Describe(agent))
    {
        Agent = agent;
    }

    public SubAgentDefinition Agent { get; }

    public override Task<string> RunAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A delegation is run by the invoker, not as a tool.");

    /// <summary>The task the caller wrote, or <see langword="null"/> when it is missing or blank.</summary>
    public static string? TaskOf(string rawArguments)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments);
        return document.RootElement.TryGetProperty(TaskArgument, out var task) && task.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(task.GetString())
            ? task.GetString()
            : null;
    }

    private static ActiveTool Describe(SubAgentDefinition agent) =>
        new(agent.AgentId, ToolOrigin.Builtin, agent.Slug, agent.Slug, ToolNaming.AgentAuditServer, agent.Description, Schema);
}

/// <summary>
/// A sub-agent failed and its edge says the failure ends the turn (spec 013 D-13). Thrown to the invoker, which records the call as
/// failed and ends the answer with the message.
/// </summary>
internal sealed class SubAgentAbortException : Exception
{
    public SubAgentAbortException()
    {
    }

    public SubAgentAbortException(string message)
        : base(message)
    {
    }

    public SubAgentAbortException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
