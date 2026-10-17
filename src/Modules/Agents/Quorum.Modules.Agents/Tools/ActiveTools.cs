using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>A tool of an assistant as stored: what the model sees (name, description, schema) and what the invoker needs to audit and check it.</summary>
internal sealed record ActiveTool(Guid ToolId, ToolOrigin Origin, string Name, string ExposedName, string? ServerName, string Description, string InputSchema);

/// <summary>The limits of the tool-calling loop for one answer, read once from the tenant's settings.</summary>
internal sealed record ToolLimits(int MaxRounds, TimeSpan TotalTimeout, TimeSpan CallTimeout, int MaxResultCharacters)
{
    public static ToolLimits From(ToolSettings settings) => new(
        settings.MaxRounds,
        TimeSpan.FromSeconds(settings.TotalTimeoutSeconds),
        TimeSpan.FromSeconds(settings.CallTimeoutSeconds),
        settings.MaxResultCharacters);

    public static ToolLimits Default { get; } = From(ToolSettings.CreateDefault(Guid.Empty));

    /// <summary>For an answer without tools: one round and no time limit beyond what the model server imposes, as before tools existed.</summary>
    public static ToolLimits Unbounded { get; } = new(1, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan, int.MaxValue);
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
            from tool in ToolPool.For(db)
            join activation in db.AssistantTools on tool.Id equals activation.ToolId
            where activation.AssistantId == assistantId
            select tool)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return tools
            .Select(tool => new ActiveTool(
                tool.Id,
                tool.Origin,
                tool.Name,
                ToolPool.ExposedName(tool),
                ServerName: null,
                tool.Description,
                tool.InputSchema))
            .OrderBy(tool => tool.ExposedName, StringComparer.Ordinal)
            .ToList();
    }
}
