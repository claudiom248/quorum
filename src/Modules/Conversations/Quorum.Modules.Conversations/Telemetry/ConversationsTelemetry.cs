using System.Diagnostics;
using Quorum.Shared.Telemetry;

namespace Quorum.Modules.Conversations.Telemetry;

/// <summary>
/// The activity source of the Conversations module and the names of its spans (spec 016 D-8). The format and the size of an
/// attachment, never its content or its name.
/// </summary>
internal static class ConversationsTelemetry
{
    public static readonly ActivitySource Source = new(QuorumActivityNames.ConversationsSource);

    public const string AttachmentExtractionActivityName = "quorum.attachment_extraction";
    public const string AttachmentFormatTag = "quorum.attachment.format";
    public const string AttachmentSizeTag = "quorum.attachment.size_bytes";
}
