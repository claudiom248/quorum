namespace Quorum.Modules.Agents.Domain;

internal sealed class Assistant
{
    private Assistant()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    public string SystemPrompt { get; private set; } = null!;

    public Guid ModelEndpointId { get; private set; }

    public static Assistant Create(Guid tenantId, string name, string systemPrompt, Guid modelEndpointId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        TenantId = tenantId,
        Name = name,
        SystemPrompt = systemPrompt,
        ModelEndpointId = modelEndpointId,
    };

    public void ChangeSystemPrompt(string systemPrompt) => SystemPrompt = systemPrompt;
}
