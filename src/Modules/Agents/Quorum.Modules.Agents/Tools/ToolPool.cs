using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// The tools an assistant of the current tenant can use: enabled ones. Activation (<see cref="AssistantTool"/>) picks from
/// this pool, and visibility scopes narrow it when MCP servers arrive.
/// </summary>
internal static class ToolPool
{
    public static IQueryable<Tool> For(AgentsDbContext db) => db.Tools.Where(t => t.State == ToolState.Enabled);

    public static ToolDefinition Definition(Tool tool) => new(ExposedName(tool), tool.Description, tool.InputSchema);

    public static string ExposedName(Tool tool) =>
        tool.Origin == ToolOrigin.Native ? ToolNaming.Native(tool.Name) : throw new NotSupportedException("MCP tools are exposed by their server.");
}
