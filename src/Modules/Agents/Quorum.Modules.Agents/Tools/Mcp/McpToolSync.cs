using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Modules.Agents.Tools.Mcp;

/// <summary>The names a refresh touched: tools that appeared, tools whose definition changed, tools the server no longer lists.</summary>
internal sealed record ToolSyncResult(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed);

/// <summary>Brings the tools stored for a server in line with what it lists now (D-14).</summary>
internal static class McpToolSync
{
    private const int MaxToolNameLength = 128;

    /// <param name="stored">The server's tools, tracked by <paramref name="db"/>.</param>
    public static ToolSyncResult Apply(AgentsDbContext db, McpServer server, IReadOnlyCollection<Tool> stored, IReadOnlyList<DiscoveredTool> listed)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(listed);

        var byName = stored.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<string>();
        var changed = new List<string>();

        foreach (var tool in listed)
        {
            // A server that repeats a name keeps its first definition; one with an unusable name offers nothing we can store.
            if (tool.Name.Length is 0 or > MaxToolNameLength || !seen.Add(tool.Name))
            {
                continue;
            }

            var hash = ToolDefinitionHash.Compute(tool.Name, tool.Description, tool.InputSchema);
            if (!byName.TryGetValue(tool.Name, out var existing))
            {
                db.Tools.Add(Tool.CreateMcp(server.TenantId, server.Id, tool.Name, tool.Description, tool.InputSchema, hash, tool.Annotations));
                added.Add(tool.Name);
            }
            else if (existing.DefinitionHash != hash)
            {
                existing.RedefineFromServer(tool.Description, tool.InputSchema, hash, tool.Annotations);
                changed.Add(tool.Name);
            }
            else
            {
                existing.UpdateAnnotations(tool.Annotations);
            }
        }

        var removed = stored.Where(t => !seen.Contains(t.Name)).ToList();
        // The activations go with their tool (cascade in the database).
        db.Tools.RemoveRange(removed);

        return new ToolSyncResult(added, changed, [.. removed.Select(t => t.Name)]);
    }
}
