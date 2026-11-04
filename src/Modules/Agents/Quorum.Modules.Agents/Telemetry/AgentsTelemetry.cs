using System.Diagnostics;
using Quorum.Modules.Agents.Tools;
using Quorum.Shared.Telemetry;

namespace Quorum.Modules.Agents.Telemetry;

/// <summary>
/// The activity source of the Agents module and the names of what it adds to the spans of the frameworks (spec 016 D-7, D-9, D-13).
/// Identifiers only: GUIDs and technical names, never text.
/// </summary>
internal static class AgentsTelemetry
{
    public static readonly ActivitySource Source = new(QuorumActivityNames.AgentsSource);

    public const string TurnActivityName = "quorum.chat_turn";
    public const string FirstTokenEvent = "quorum.first_token";

    public const string StopReasonTag = "quorum.stop_reason";
    public const string RoundsTag = "quorum.rounds";
    public const string ToolCallsTag = "quorum.tool_calls";

    public const string StopCompleted = "completed";
    public const string StopCancelled = "cancelled";
    public const string StopRounds = "rounds";
    public const string StopTotalTime = "total_time";
    public const string StopError = "error";

    /// <summary>The identifiers of an answer, for its spans and its logs alike.</summary>
    public static Dictionary<string, object> Identifiers(ToolRunContext context) => new(StringComparer.Ordinal)
    {
        [QuorumActivityNames.TenantIdTag] = context.TenantId,
        [QuorumActivityNames.UserIdTag] = context.UserId,
        [QuorumActivityNames.ConversationIdTag] = context.ConversationId,
        [QuorumActivityNames.AssistantIdTag] = context.AssistantId,
        [QuorumActivityNames.AgentIdTag] = context.AgentId,
    };

    /// <summary>Sets the identifiers on the root span before any child starts: the children copy them when they start (the Host's processor).</summary>
    public static void TagTurn(Activity activity, ToolRunContext context)
    {
        foreach (var (key, value) in Identifiers(context))
        {
            activity.SetTag(key, value);
        }
    }
}
