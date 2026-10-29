namespace Quorum.Modules.Agents.Domain;

/// <summary>How a lookup tool is offered to the model on one assistant (spec 012 D-8).</summary>
internal static class LookupManifest
{
    /// <summary>The functions of spec 007: values, documents and count, each with its full parameters.</summary>
    public const string Full = "Full";

    /// <summary>Two functions: describe, which tells the lists and knowledge bases, and query, which reads one of them.</summary>
    public const string Lean = "Lean";

    public static bool IsValid(string? manifest) => manifest is Full or Lean;
}

/// <summary>A tool the admin activated on an assistant: only active tools are offered to the model.</summary>
internal sealed class AssistantTool
{
    private AssistantTool()
    {
    }

    public Guid AssistantId { get; private set; }

    public Guid ToolId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Only meaningful for a lookup tool (spec 012 D-8); the other tools have the default.</summary>
    public string Manifest { get; private set; } = LookupManifest.Full;

    public static AssistantTool Create(Guid tenantId, Guid assistantId, Guid toolId) => new()
    {
        TenantId = tenantId,
        AssistantId = assistantId,
        ToolId = toolId,
    };

    public void SetManifest(string manifest) => Manifest = manifest;
}
