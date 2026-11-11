using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools.Lookup;
using Quorum.Modules.Agents.Tools.Rag;
using Quorum.Modules.Agents.Tools.WebFetch;
using Quorum.Modules.Agents.Tools.WebSearch;

namespace Quorum.Modules.Agents.Tools;

/// <summary>A tool of an agent as stored: what the model sees (name, description, schema) and what the invoker needs to audit, check and run it.</summary>
/// <param name="Server">Where an MCP tool runs; <see langword="null"/> for built-in, rag and lookup tools.</param>
/// <param name="Rag">The binding of a rag tool; <see langword="null"/> for the others.</param>
/// <param name="Lookup">The binding of a lookup tool; <see langword="null"/> for the others.</param>
/// <param name="WebSearch">The binding of a web search tool; <see langword="null"/> for the others.</param>
/// <param name="WebFetch">The binding of a web fetch tool; <see langword="null"/> for the others.</param>
/// <param name="DefinitionHash">The hash of the definition the model is offered; a call is refused when the stored one differs by then.</param>
internal sealed record ActiveTool(
    Guid ToolId,
    ToolOrigin Origin,
    string Name,
    string ExposedName,
    string? ServerName,
    string Description,
    string InputSchema,
    string? DefinitionHash = null,
    McpServerEndpoint? Server = null,
    RagBinding? Rag = null,
    LookupBinding? Lookup = null,
    WebSearchBinding? WebSearch = null,
    WebFetchBinding? WebFetch = null);

/// <summary>What a call needs to reach an MCP server: its identity, address and the encrypted static headers.</summary>
internal sealed record McpServerEndpoint(Guid ServerId, string Url, string? ProtectedHeaders);

/// <summary>The limits of the tool-calling loop for one answer, read once from the tenant's settings.</summary>
internal sealed record ToolLimits(int MaxRounds, TimeSpan TotalTimeout, TimeSpan CallTimeout, int MaxResultCharacters, TimeSpan ConfirmationTimeout)
{
    public static ToolLimits From(ToolSettings settings) => new(
        settings.MaxRounds,
        TimeSpan.FromSeconds(settings.TotalTimeoutSeconds),
        TimeSpan.FromSeconds(settings.CallTimeoutSeconds),
        settings.MaxResultCharacters,
        TimeSpan.FromSeconds(settings.ConfirmationTimeoutSeconds));

    public static ToolLimits Default { get; } = From(ToolSettings.CreateDefault(Guid.Empty));

    /// <summary>For an answer without tools: one round and no time limit beyond what the model server imposes, as before tools existed.</summary>
    public static ToolLimits Unbounded { get; } = new(1, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan, int.MaxValue, Timeout.InfiniteTimeSpan);
}

/// <summary>Who is answering what, for the invoker and the audit log.</summary>
internal sealed record ToolRunContext(
    Guid TenantId,
    Guid UserId,
    Guid AssistantId,
    Guid AgentId,
    Guid ConversationId,
    ToolLimits Limits);

/// <summary>Reads the platform's one web configuration at most once for whoever shares the reader.</summary>
internal sealed class WebConfigurationReader(AgentsDbContext db)
{
    private bool _read;
    private WebConfiguration? _value;

    public async Task<WebConfiguration?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_read)
        {
            _value = await db.WebConfigurations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            _read = true;
        }

        return _value;
    }
}

/// <summary>The tools the agents of an assistant have activated, as the runtime offers them (spec 013 AC-25, spec 012 D-8).</summary>
internal static class AgentToolLoader
{
    /// <summary>The tools of the agent that are in its pool and activated, in a stable order.</summary>
    public static async Task<IReadOnlyList<ActiveTool>> LoadAsync(AgentsDbContext db, Guid agentId, CancellationToken cancellationToken, WebConfigurationReader? web = null) =>
        (await LoadManyAsync(db, [agentId], cancellationToken, web)).GetValueOrDefault(agentId) ?? [];

    /// <summary>The tools of each agent, for all of them with one query. An agent with none is absent.</summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ActiveTool>>> LoadManyAsync(
        AgentsDbContext db,
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken cancellationToken,
        WebConfigurationReader? web = null)
    {
        if (agentIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<ActiveTool>>();
        }

        // The activations of an agent another tenant shares are the owner's, and the pool is worked out for that owner: the query says so.
        var rows = await ToolPool.ActivatedFor(db, agentIds).ToListAsync(cancellationToken);

        // A tool that never overrode a field inherits it now, not when it was created: the row is read with the tool, so an
        // administrator changing the Settings tabs reaches the tools that follow it (spec 006, AC-13).
        var webConfiguration = rows.Any(row => row.WebFetch is not null)
            ? await (web ?? new WebConfigurationReader(db)).ReadAsync(cancellationToken)
            : null;

        return rows
            .GroupBy(row => row.AgentId)
            .ToDictionary(group => group.Key, group => ActiveToolsOf(group.Select(row => ToActiveTool(row, webConfiguration))));
    }

    // Names are checked when a tool or an agent is saved, but older data may hold two that the model would take for one: the oldest keeps the name.
    private static IReadOnlyList<ActiveTool> ActiveToolsOf(IEnumerable<ActiveTool> tools) =>
        [.. tools
            .OrderBy(tool => tool.ToolId)
            .DistinctBy(tool => tool.ExposedName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(tool => tool.ExposedName, StringComparer.Ordinal)];

    private static ActiveTool ToActiveTool(PoolRow row, WebConfiguration? webConfiguration)
    {
        var webSearch = row.WebSearch is null ? null : WebSearchBinding.From(row.WebSearch);
        var webFetch = row.WebFetch is null ? null : WebFetchBinding.From(row.WebFetch, webConfiguration);
        var lookup = row.Lookup is null ? null : LookupBinding.From(row.Lookup) with { Manifest = row.Manifest ?? LookupManifest.Full };

        // A web search or fetch tool's definition comes from its binding, the way a rag tool's comes from the catalog; unlike
        // a rag tool's it needs nothing from outside the row, so it is written here and not by a resolver.
        var (description, schema) = webSearch is not null
            ? WebSearchDefinition(row.Tool, webSearch)
            : webFetch is not null
                ? WebFetchDefinition(row.Tool, webFetch)
                : (row.Tool.Description, row.Tool.InputSchema);

        return new ActiveTool(
            row.Tool.Id,
            row.Tool.Origin,
            row.Tool.Name,
            PoolTool.ExposedNameOf(row.Tool, row.ServerName),
            AuditServerOf(row),
            description,
            schema,
            row.Tool.DefinitionHash,
            row.ServerId is { } serverId && row.ServerUrl is not null ? new McpServerEndpoint(serverId, row.ServerUrl, row.ProtectedHeaders) : null,
            row.Rag is null ? null : RagBinding.From(row.Rag),
            lookup,
            webSearch,
            webFetch);
    }

    private static (string Description, string InputSchema) WebSearchDefinition(Tool tool, WebSearchBinding binding)
    {
        var generated = WebSearchToolDefinition.Build(tool.Description, binding.TopN);
        return (generated.Description, generated.InputSchema);
    }

    private static (string Description, string InputSchema) WebFetchDefinition(Tool tool, WebFetchBinding binding)
    {
        var generated = WebFetchToolDefinition.Build(tool.Description, binding.MaxUrlsPerInvocation);
        return (generated.Description, generated.InputSchema);
    }

    // The audit log stores these names as the server of the call, which is how a rag, lookup or web tool is told apart in it.
    private static string? AuditServerOf(PoolRow row) => row.Tool.Origin switch
    {
        ToolOrigin.Rag => ToolNaming.RagAuditServer,
        ToolOrigin.Lookup => ToolNaming.LookupAuditServer,
        ToolOrigin.WebSearch => ToolNaming.WebSearchAuditServer,
        ToolOrigin.WebFetch => ToolNaming.WebFetchAuditServer,
        _ => row.ServerName,
    };
}
