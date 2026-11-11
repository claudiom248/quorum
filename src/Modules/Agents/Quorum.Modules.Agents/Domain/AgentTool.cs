namespace Quorum.Modules.Agents.Domain;

/// <summary>How a lookup tool is offered to the model on one agent (spec 012 D-8, spec 013 D-42).</summary>
internal static class LookupManifest
{
    /// <summary>The functions of spec 007: values, documents and count, each with its full parameters.</summary>
    public const string Full = "Full";

    /// <summary>Two functions: describe, which tells the lists and knowledge bases, and query, which reads one of them.</summary>
    public const string Lean = "Lean";

    public static bool IsValid(string? manifest) => manifest is Full or Lean;
}

/// <summary>
/// Whether a consumer of an assistant may turn a tool off (spec 018 D-3, D-4). Required tools are always on for consumers;
/// optional ones are on by default and can be turned off when the conversation is created.
/// </summary>
internal enum ToolMode
{
    Required,
    Optional,
}

/// <summary>A tool the admin activated on an agent: only active tools are offered to the model.</summary>
internal sealed class AgentTool
{
    private AgentTool()
    {
    }

    public Guid AgentId { get; private set; }

    public Guid ToolId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Only meaningful for a lookup tool (spec 012 D-8); the other tools have the default.</summary>
    public string Manifest { get; private set; } = LookupManifest.Full;

    /// <summary>Required unless the administrator says otherwise (spec 018 D-19): existing attachments keep their behaviour.</summary>
    public ToolMode Mode { get; private set; } = ToolMode.Required;

    public static AgentTool Create(Guid tenantId, Guid agentId, Guid toolId) => new()
    {
        TenantId = tenantId,
        AgentId = agentId,
        ToolId = toolId,
    };

    public void SetManifest(string manifest) => Manifest = manifest;

    public void SetMode(ToolMode mode) => Mode = mode;
}
