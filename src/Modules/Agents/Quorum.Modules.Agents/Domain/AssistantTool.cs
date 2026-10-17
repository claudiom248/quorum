namespace Quorum.Modules.Agents.Domain;

/// <summary>A tool the admin activated on an assistant: only active tools are offered to the model.</summary>
internal sealed class AssistantTool
{
    private AssistantTool()
    {
    }

    public Guid AssistantId { get; private set; }

    public Guid ToolId { get; private set; }

    public Guid TenantId { get; private set; }

    public static AssistantTool Create(Guid tenantId, Guid assistantId, Guid toolId) => new()
    {
        TenantId = tenantId,
        AssistantId = assistantId,
        ToolId = toolId,
    };
}
