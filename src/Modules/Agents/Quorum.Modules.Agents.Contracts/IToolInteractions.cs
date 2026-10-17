using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Contracts;

/// <summary>
/// Hands the signed-in user's reply to an answer that is waiting for it: the decision on a tool call that needs confirmation, or
/// the answer to a question of the model. The caller has already checked that the conversation is the user's.
/// </summary>
public interface IToolInteractions
{
    /// <summary>Approves or denies a pending confirmation.</summary>
    /// <returns>
    /// <c>tool.interaction_not_found</c> when no such confirmation is pending for this user in this conversation,
    /// <c>tool.already_decided</c> when it was decided already.
    /// </returns>
    Result Decide(Guid conversationId, Guid callId, bool approved);

    /// <summary>Answers a pending question of the model.</summary>
    /// <returns>The same errors as <see cref="Decide"/>.</returns>
    Result Answer(Guid conversationId, Guid callId, string answer);
}
