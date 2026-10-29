using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools.Lookup;

/// <summary>The roles a knowledge base can play in a lookup tool (spec 012 A1-1): only a document knowledge base is read live.</summary>
internal static class LookupToolRoles
{
    public static bool IsDocument(KnowledgeBaseInfo knowledgeBase) => knowledgeBase.MapsText || knowledgeBase.MapsDocumentId;
}
