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
}
