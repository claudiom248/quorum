using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools.Lookup;
using Quorum.Modules.Agents.Tools.Rag;
using Quorum.Modules.Agents.Tools.WebFetch;
using Quorum.Modules.Agents.Tools.WebSearch;

namespace Quorum.Modules.Agents.Tools;

/// <summary>A tool of an assistant as stored: what the model sees (name, description, schema) and what the invoker needs to audit, check and run it.</summary>
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
    Guid ConversationId,
    ToolLimits Limits);

internal static class ActiveToolsQuery
{
    /// <summary>The tools of the assistant that are in its pool and activated, in a stable order.</summary>
    public static async Task<IReadOnlyList<ActiveTool>> LoadAsync(AgentsDbContext db, Guid assistantId, CancellationToken cancellationToken)
    {
        var tools = await (
            from pooled in ToolPool.For(db, assistantId)
            join activation in db.AssistantTools on pooled.Tool.Id equals activation.ToolId
            where activation.AssistantId == assistantId
            select pooled)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // The encrypted headers are read only here, for the tools that will run, and not by every listing of the pool.
        var serverIds = tools.Select(pooled => pooled.Tool.ServerId).OfType<Guid>().Distinct().ToList();
        var endpoints = serverIds.Count == 0
            ? []
            : await db.McpServers.IgnoreQueryFilters()
                .Where(server => serverIds.Contains(server.Id))
                .Select(server => new McpServerEndpoint(server.Id, server.Url, server.ProtectedHeaders))
                .ToDictionaryAsync(endpoint => endpoint.ServerId, cancellationToken);

        var ragIds = tools.Where(pooled => pooled.Tool.Origin == ToolOrigin.Rag).Select(pooled => pooled.Tool.Id).ToList();
        var bindings = ragIds.Count == 0
            ? []
            : await db.RagTools.IgnoreQueryFilters().AsNoTracking()
                .Where(rag => ragIds.Contains(rag.ToolId))
                .ToDictionaryAsync(rag => rag.ToolId, RagBinding.From, cancellationToken);

        var lookupIds = tools.Where(pooled => pooled.Tool.Origin == ToolOrigin.Lookup).Select(pooled => pooled.Tool.Id).ToList();
        var lookupBindings = lookupIds.Count == 0
            ? []
            : await db.LookupTools.IgnoreQueryFilters().AsNoTracking()
                .Where(lookup => lookupIds.Contains(lookup.ToolId))
                .ToDictionaryAsync(lookup => lookup.ToolId, LookupBinding.From, cancellationToken);

        var webSearchIds = tools.Where(pooled => pooled.Tool.Origin == ToolOrigin.WebSearch).Select(pooled => pooled.Tool.Id).ToList();
        var webSearchBindings = webSearchIds.Count == 0
            ? []
            : await db.WebSearchTools.IgnoreQueryFilters().AsNoTracking()
                .Where(webSearch => webSearchIds.Contains(webSearch.ToolId))
                .ToDictionaryAsync(webSearch => webSearch.ToolId, WebSearchBinding.From, cancellationToken);

        var webFetchIds = tools.Where(pooled => pooled.Tool.Origin == ToolOrigin.WebFetch).Select(pooled => pooled.Tool.Id).ToList();
        var webFetchBindings = webFetchIds.Count == 0
            ? []
            : await db.WebFetchTools.IgnoreQueryFilters().AsNoTracking()
                .Where(webFetch => webFetchIds.Contains(webFetch.ToolId))
                .ToDictionaryAsync(webFetch => webFetch.ToolId, WebFetchBinding.From, cancellationToken);

        return tools
            .Select(pooled =>
            {
                var webSearch = webSearchBindings.GetValueOrDefault(pooled.Tool.Id);
                var webFetch = webFetchBindings.GetValueOrDefault(pooled.Tool.Id);

                // A web search or fetch tool's definition comes from its binding, the way a rag tool's comes from the catalog; unlike
                // a rag tool's it needs nothing from outside the row, so it is written here and not by a resolver.
                string generatedDescription = pooled.Tool.Description;
                string generatedSchema = pooled.Tool.InputSchema;
                if (webSearch is not null)
                {
                    var generatedSearch = WebSearchToolDefinition.Build(pooled.Tool.Description, webSearch.TopN);
                    generatedDescription = generatedSearch.Description;
                    generatedSchema = generatedSearch.InputSchema;
                }
                else if (webFetch is not null)
                {
                    var generatedFetch = WebFetchToolDefinition.Build(pooled.Tool.Description, webFetch.MaxUrlsPerInvocation);
                    generatedDescription = generatedFetch.Description;
                    generatedSchema = generatedFetch.InputSchema;
                }

                return new ActiveTool(
                    pooled.Tool.Id,
                    pooled.Tool.Origin,
                    pooled.Tool.Name,
                    pooled.ExposedName,
                    AuditServerOf(pooled),
                    generatedDescription,
                    generatedSchema,
                    pooled.Tool.DefinitionHash,
                    pooled.Tool.ServerId is { } serverId && endpoints.TryGetValue(serverId, out var endpoint) ? endpoint : null,
                    bindings.GetValueOrDefault(pooled.Tool.Id),
                    lookupBindings.GetValueOrDefault(pooled.Tool.Id),
                    webSearch,
                    webFetch);
            })
            .OrderBy(tool => tool.ExposedName, StringComparer.Ordinal)
            .ToList();
    }

    private static string? AuditServerOf(PoolTool pooled) => pooled.Tool.Origin switch
    {
        ToolOrigin.Rag => ToolNaming.RagAuditServer,
        ToolOrigin.Lookup => ToolNaming.LookupAuditServer,
        ToolOrigin.WebSearch => ToolNaming.WebSearchAuditServer,
        ToolOrigin.WebFetch => ToolNaming.WebFetchAuditServer,
        _ => pooled.ServerName,
    };
}
