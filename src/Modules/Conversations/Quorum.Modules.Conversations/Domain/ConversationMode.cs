namespace Quorum.Modules.Conversations.Domain;

/// <summary>Who a conversation is for (spec 018 D-12): a consumer, or the administrator's debug chat.</summary>
internal enum ConversationMode
{
    Consumer,
    Debug,
}
