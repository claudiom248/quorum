namespace Quorum.Shared.Telemetry;

/// <summary>
/// The names Quorum emits telemetry under. They live here, not in the Host, so that every module and the composition root agree
/// on them without a reference between modules. Names only: no behaviour.
/// </summary>
public static class QuorumActivityNames
{
    public const string ServiceName = "quorum";

    /// <summary>One activity source per module (spec 016 D-22).</summary>
    public const string AgentsSource = "Quorum.Agents";
    public const string ConversationsSource = "Quorum.Conversations";
    public const string KnowledgeBasesSource = "Quorum.KnowledgeBases";

    /// <summary>The sources of the instrumented libraries, registered by the names they publish.</summary>
    public const string MicrosoftExtensionsAiSource = "Experimental.Microsoft.Extensions.AI";
    public const string MicrosoftAgentsAiSource = "Experimental.Microsoft.Agents.AI";

    // Identifiers only (spec 016 D-9): GUIDs and the technical agent slug, never names, emails or text.
    public const string TenantIdTag = "quorum.tenant.id";
    public const string UserIdTag = "quorum.user.id";
    public const string ConversationIdTag = "quorum.conversation.id";
    public const string AssistantIdTag = "quorum.assistant.id";
    public const string AgentIdTag = "quorum.agent.id";
    public const string AgentSlugTag = "quorum.agent.slug";
}
