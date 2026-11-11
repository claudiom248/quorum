namespace Quorum.Modules.Agents.Contracts;

/// <summary>
/// What Conversations needs to know about assistants and their tools before it stores a conversation (spec 018 D-7, D-11, D-16).
/// Plain values only: tool names as the model sees them, and no Microsoft.Extensions.AI type.
/// </summary>
public interface IChatAssistantAccess
{
    /// <summary>Checks a tool selection for a new conversation (consumer) or for a debug one (any attached root tool).</summary>
    Task<ChatSelectionCheck> CheckSelectionAsync(Guid assistantId, bool debug, IReadOnlyList<string> disabledTools, CancellationToken cancellationToken);

    /// <summary>Checks an endpoint override of a debug conversation: it exists in the tenant and can carry the root's tools (D-16).</summary>
    Task<ChatOverrideCheck> CheckOverrideAsync(Guid assistantId, Guid modelEndpointId, CancellationToken cancellationToken);
}

public enum ChatSelectionProblem
{
    None,

    /// <summary>The assistant does not exist, or is not exposed to consumers (spec 018 D-11: the same answer for both).</summary>
    NotFound,

    /// <summary>The assistant does not allow consumers to choose tools, and the request named some.</summary>
    SelectionNotAllowed,

    /// <summary>Names that are required, unknown or not attached to the root agent; <see cref="ChatSelectionCheck.Names"/> lists them.</summary>
    InvalidNames,
}

public sealed record ChatSelectionCheck(ChatSelectionProblem Problem, IReadOnlyList<string> Names);

public enum ChatOverrideProblem
{
    None,

    /// <summary>The endpoint does not exist in the tenant.</summary>
    EndpointNotFound,

    /// <summary>The endpoint does not support tools, but the root agent has tools or sub-agents.</summary>
    WithoutTools,
}

public sealed record ChatOverrideCheck(ChatOverrideProblem Problem);
