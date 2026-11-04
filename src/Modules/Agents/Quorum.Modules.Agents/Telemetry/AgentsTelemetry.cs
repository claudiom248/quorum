using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

    public const string AuditRetentionActivityName = "quorum.background.audit_retention";
    public const string AuditPurgedTag = "quorum.audit.purged";

    public const string EmbeddingActivityName = "quorum.embedding";
    public const string EmbeddingModelTag = "quorum.embedding.model";
    public const string ResultCountTag = "quorum.result_count";

    public const string ConfirmationActivityName = "quorum.confirmation";
    public const string ConfirmationDecisionTag = "quorum.decision";

    public const string ToolOriginTag = "quorum.tool.origin";
    public const string ToolRoundTag = "quorum.tool.round";
    public const string ToolArgumentsHashTag = "quorum.tool.arguments_hash";
    public const string ToolDecisionTag = "quorum.tool.decision";
    public const string ToolOutcomeTag = "quorum.tool.outcome";
    public const string ErrorCodeTag = "quorum.error.code";

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

    /// <summary>
    /// A short hash of a call's arguments: the same call repeated shows the same hash on its spans, without the arguments themselves
    /// (spec 016 D-15). Not a secret store: a hash of a short argument can be guessed, which the spec accepts.
    /// </summary>
    public static string ArgumentsHash(string arguments) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(arguments)))[..16].ToLowerInvariant();

    /// <summary>Sets the identifiers on the root span before any child starts: the children copy them when they start (the Host's processor).</summary>
    public static void TagTurn(Activity activity, ToolRunContext context)
    {
        foreach (var (key, value) in Identifiers(context))
        {
            activity.SetTag(key, value);
        }
    }
}
