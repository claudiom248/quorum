using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>A tool of an assistant as stored: what the model sees (name, description, schema) and what the invoker needs to audit, check and run it.</summary>
/// <param name="Server">Where an MCP tool runs; <see langword="null"/> for native tools.</param>
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
    McpServerEndpoint? Server = null);

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

        return tools
            .Select(pooled => new ActiveTool(
                pooled.Tool.Id,
                pooled.Tool.Origin,
                pooled.Tool.Name,
                pooled.ExposedName,
                pooled.ServerName,
                pooled.Tool.Description,
                pooled.Tool.InputSchema,
                pooled.Tool.DefinitionHash,
                pooled.Tool.ServerId is { } serverId && endpoints.TryGetValue(serverId, out var endpoint) ? endpoint : null))
            .OrderBy(tool => tool.ExposedName, StringComparer.Ordinal)
            .ToList();
    }
}
