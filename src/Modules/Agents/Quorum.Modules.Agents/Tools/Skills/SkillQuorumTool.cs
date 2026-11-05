using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Domain;

namespace Quorum.Modules.Agents.Tools.Skills;

/// <summary>One skill as a turn holds it, copied when the turn starts (spec 017 D-17): the body is read from here, never from the database.</summary>
internal sealed record SkillSnapshotEntry(Guid SkillId, string Slug, string Description, string Body);

/// <summary>
/// The <c>load_skill</c> tool of an agent with skills (spec 017 D-3, D-4). It returns the body of an assigned skill and runs nothing else:
/// <see cref="ToolCallInvoker"/> audits it, applies the result size rule and records it in the ledger like any other tool, with no
/// confirmation (D-7). It is not stored as an activation: the runtime builds it from the tenant's built-in row when the agent has skills.
/// </summary>
internal sealed partial class SkillQuorumTool : QuorumTool
{
    public const string NameArgument = "name";

    private const int MaxLoggedNameLength = 64;

    private readonly Guid _agentId;
    private readonly ILogger _logger;

    /// <param name="skills">The skills of the agent this turn, ordered by slug.</param>
    /// <param name="agentId">The agent that holds them, for the log.</param>
    public SkillQuorumTool(ActiveTool descriptor, IReadOnlyList<SkillSnapshotEntry> skills, Guid agentId, ILogger logger)
        : base(descriptor)
    {
        Skills = skills;
        _agentId = agentId;
        _logger = logger;
    }

    public IReadOnlyList<SkillSnapshotEntry> Skills { get; }

    public override Task<string> RunAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var name = arguments.TryGetProperty(NameArgument, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (Skills.FirstOrDefault(skill => string.Equals(skill.Slug, name, StringComparison.Ordinal)) is { } found)
        {
            return Task.FromResult(found.Body);
        }

        LogUnknownSkill(_logger, _agentId, name is { Length: > MaxLoggedNameLength } ? name[..MaxLoggedNameLength] : name ?? string.Empty);

        // The model corrects itself from this list (spec 017 D-9); the slugs are not secrets, the bodies are not listed.
        var available = Skills.Count == 0 ? "none" : string.Join(", ", Skills.Select(skill => skill.Slug));
        throw new ToolFailedException($"There is no skill named '{name}' for this agent. The skills available to it are: {available}.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The agent {AgentId} asked load_skill for the skill {Skill}, which it does not have.")]
    private static partial void LogUnknownSkill(ILogger logger, Guid agentId, string skill);
}
