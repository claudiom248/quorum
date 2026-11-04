using System.Diagnostics;
using OpenTelemetry;
using Quorum.Shared.Telemetry;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Copies the Quorum identifiers from a span's parent when the span starts, so that the framework spans of a turn carry them too
/// without the frameworks knowing about Quorum (spec 016 D-7, D-9). Tags set on a parent after its children started do not reach them.
/// </summary>
internal sealed class QuorumTagsProcessor : BaseProcessor<Activity>
{
    private static readonly string[] Inherited =
    [
        QuorumActivityNames.TenantIdTag,
        QuorumActivityNames.UserIdTag,
        QuorumActivityNames.ConversationIdTag,
        QuorumActivityNames.AssistantIdTag,
        QuorumActivityNames.AgentIdTag,
        QuorumActivityNames.AgentSlugTag,
    ];

    public override void OnStart(Activity activity)
    {
        var parent = activity.Parent;
        if (parent is null)
        {
            return;
        }

        foreach (var key in Inherited)
        {
            var value = parent.GetTagItem(key);
            if (value is not null)
            {
                activity.SetTag(key, value);
            }
        }
    }
}
