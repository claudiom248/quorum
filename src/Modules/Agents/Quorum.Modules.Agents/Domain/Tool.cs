namespace Quorum.Modules.Agents.Domain;

internal enum ToolOrigin
{
    Native,
    Mcp,
}

internal enum ToolState
{
    /// <summary>New or changed since the admin last looked: not offered to any model until reviewed.</summary>
    PendingReview,
    Enabled,
    Disabled,
}

internal enum ToolPolicy
{
    /// <summary>Runs as soon as the model asks.</summary>
    Auto,

    /// <summary>Waits for the user's explicit approval.</summary>
    Confirm,
}

/// <summary>A capability the model can call: an in-process native tool or a tool discovered on an MCP server.</summary>
internal sealed class Tool
{
    private Tool()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public ToolOrigin Origin { get; private set; }

    /// <summary>The tool's own name, without the origin prefix the model sees.</summary>
    public string Name { get; private set; } = null!;

    public string Description { get; private set; } = null!;

    /// <summary>JSON Schema of the arguments, kept as the text it was defined with.</summary>
    public string InputSchema { get; private set; } = null!;

    public ToolState State { get; private set; }

    public ToolPolicy Policy { get; private set; }

    public static Tool CreateNative(Guid tenantId, string name, string description, string inputSchema) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Origin = ToolOrigin.Native,
        Name = name,
        Description = description,
        InputSchema = inputSchema,
        State = ToolState.Enabled,
        Policy = ToolPolicy.Auto,
    };

    /// <summary>Follows a new release's definition of a native tool; what the admin decided (state, policy) is kept.</summary>
    public void Redefine(string description, string inputSchema)
    {
        Description = description;
        InputSchema = inputSchema;
    }

    public void Review(ToolState state, ToolPolicy policy)
    {
        State = state;
        Policy = policy;
    }
}
