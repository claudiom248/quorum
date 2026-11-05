namespace Quorum.Shared.Endpoints;

/// <summary>Authorization policies any module can require; the Tenancy module defines them.</summary>
public static class QuorumPolicies
{
    public const string Admin = "admin";

    /// <summary>Register tool servers, review tools, set policies and scopes, read the audit log, change tool settings.</summary>
    public const string ToolsManage = "tools.manage";

    /// <summary>Activate the tools of the pool on an assistant.</summary>
    public const string ToolsAttach = "tools.attach";

    /// <summary>Invoke tools by chatting with an assistant that has them.</summary>
    public const string ToolsUse = "tools.use";

    /// <summary>Manage model endpoints, agents and their sub-agent edges, and the assistants that point to them.</summary>
    public const string AgentsManage = "agents.manage";

    /// <summary>Manage the skills of the tenant: read, write, import, export, and the display name style (spec 017 D-15).</summary>
    public const string SkillsManage = "skills.manage";

    /// <summary>Manage the metadata dictionary and the knowledge bases: register, verify, map, try a search.</summary>
    public const string KnowledgeBaseManage = "knowledge_base.manage";

    /// <summary>Trigger and check the refresh of a knowledge base's lookups: admin or service token.</summary>
    public const string KnowledgeBaseRefresh = "knowledge_base.refresh";
}
