namespace Quorum.Modules.Agents.Domain;

internal enum ToolOrigin
{
    Builtin,
    Mcp,

    /// <summary>Configured by an administrator as a search over knowledge bases; runs in-process (spec 003).</summary>
    Rag,

    /// <summary>Configured by an administrator as a reader of lookups (lists of values of knowledge bases); runs in-process (spec 007).</summary>
    Lookup,
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

/// <summary>A capability the model can call: an in-process built-in tool or a tool discovered on an MCP server.</summary>
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

    /// <summary>The MCP server that offers the tool; <see langword="null"/> for built-in tools.</summary>
    public Guid? ServerId { get; private set; }

    /// <summary>SHA-256 of name, description and canonical input schema: how a refresh tells that a tool changed (D-14).</summary>
    public string? DefinitionHash { get; private set; }

    /// <summary>The hints the server sent (<c>readOnlyHint</c>, <c>destructiveHint</c>...) as a JSON object. Untrusted: shown to the admin, never used to decide anything.</summary>
    public string? Annotations { get; private set; }

    public static Tool CreateBuiltin(Guid tenantId, string name, string description, string inputSchema) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Origin = ToolOrigin.Builtin,
        Name = name,
        Description = description,
        InputSchema = inputSchema,
        State = ToolState.Enabled,
        Policy = ToolPolicy.Auto,
    };

    /// <summary>
    /// A rag tool. Its description is the administrator's text for the model; the schema is generated from the binding at the start of
    /// each answer, so none is stored. <paramref name="configurationHash"/> stands for the whole binding (see <c>RagToolHash</c>).
    /// </summary>
    public static Tool CreateRag(Guid tenantId, string name, string description, string configurationHash, ToolPolicy policy) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Origin = ToolOrigin.Rag,
        Name = name,
        Description = description,
        InputSchema = "{}",
        DefinitionHash = configurationHash,
        State = ToolState.Enabled,
        Policy = policy,
    };

    /// <summary>
    /// A lookup tool. As a rag tool's, its schema is generated from the binding at the start of each answer, so none is stored.
    /// <paramref name="configurationHash"/> stands for the whole binding (see <c>LookupToolHash</c>).
    /// </summary>
    public static Tool CreateLookup(Guid tenantId, string name, string description, string configurationHash, ToolPolicy policy) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Origin = ToolOrigin.Lookup,
        Name = name,
        Description = description,
        InputSchema = "{}",
        DefinitionHash = configurationHash,
        State = ToolState.Enabled,
        Policy = policy,
    };

    /// <summary>The administrator changed the binding: a call that waits on the old one finds a different hash and is refused.</summary>
    public void Reconfigure(string name, string description, string configurationHash, ToolPolicy policy)
    {
        Name = name;
        Description = description;
        DefinitionHash = configurationHash;
        Policy = policy;
    }

    /// <summary>A tool a server listed for the first time: not offered to any model until the admin reviews it, and it asks for confirmation.</summary>
    public static Tool CreateMcp(Guid tenantId, Guid serverId, string name, string description, string inputSchema, string definitionHash, string? annotations) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ServerId = serverId,
        Origin = ToolOrigin.Mcp,
        Name = name,
        Description = description,
        InputSchema = inputSchema,
        DefinitionHash = definitionHash,
        Annotations = annotations,
        State = ToolState.PendingReview,
        Policy = ToolPolicy.Confirm,
    };

    /// <summary>The server now describes the tool differently: what the admin approved no longer applies, so it goes back to review and to confirmation.</summary>
    public void RedefineFromServer(string description, string inputSchema, string definitionHash, string? annotations)
    {
        Description = description;
        InputSchema = inputSchema;
        DefinitionHash = definitionHash;
        Annotations = annotations;
        State = ToolState.PendingReview;
        Policy = ToolPolicy.Confirm;
    }

    /// <summary>Annotations are information only and are not part of the hash, so they follow the server without a new review.</summary>
    public void UpdateAnnotations(string? annotations) => Annotations = annotations;

    /// <summary>Follows a new release's definition of a built-in tool; what the admin decided (state, policy) is kept.</summary>
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
