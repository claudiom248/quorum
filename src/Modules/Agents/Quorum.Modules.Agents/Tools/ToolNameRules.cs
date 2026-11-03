using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Tools;

/// <summary>
/// The name of a tool made in Quorum (rag, lookup, web search, web fetch). People read it in the lists and the ledger and the model calls the
/// tool by it, so it is one name for both and has to be one the model APIs accept: letters of any case, digits, <c>-</c> and <c>_</c>. It never
/// holds two underscores in a row, which is how an MCP tool carries its server (<c>server__tool</c>).
/// </summary>
internal static partial class ToolNameRules
{
    public const int MaxLength = 40;

    public const string Description = "The name must be 1 to 40 characters: letters, digits, '-' and '_', starting with a letter or digit, and never two underscores in a row. People and the model read the same name.";

    public static bool IsValid(string? name) =>
        name is not null && NamePattern().IsMatch(name) && !name.Contains("__", StringComparison.Ordinal);

    /// <summary>
    /// The error when the name, or one of the functions a lookup tool would be offered as, is already what the model calls another tool of the
    /// tenant or one of its agents, whatever the case: it could not tell them apart. <see langword="null"/> when the name is free.
    /// </summary>
    /// <param name="exceptToolId">The tool being renamed, which does not clash with itself.</param>
    public static async Task<Error?> ClashAsync(AgentsDbContext db, ToolOrigin origin, string name, Guid? exceptToolId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var mine = ToolNaming.FunctionsOf(origin, name);
        var except = exceptToolId ?? Guid.Empty;
        var tools = await db.Tools.AsNoTracking()
            .Where(t => t.Origin != ToolOrigin.Mcp && t.Id != except)
            .Select(t => new { t.Origin, t.Name })
            .ToListAsync(cancellationToken);
        foreach (var tool in tools)
        {
            foreach (var function in ToolNaming.FunctionsOf(tool.Origin, tool.Name))
            {
                if (mine.Contains(function, StringComparer.OrdinalIgnoreCase))
                {
                    return Taken(function);
                }
            }
        }

        var slugs = await db.Agents.AsNoTracking().Select(a => a.Slug).ToListAsync(cancellationToken);
        var agent = slugs.FirstOrDefault(slug => mine.Contains(slug, StringComparer.OrdinalIgnoreCase));
        return agent is null ? null : Taken(agent);
    }

    /// <summary>The same for the slug of an agent, which is the name of the tool that calls it: only the tools can take it.</summary>
    public static async Task<Error?> ClashWithToolsAsync(AgentsDbContext db, string slug, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var tools = await db.Tools.AsNoTracking()
            .Where(t => t.Origin != ToolOrigin.Mcp)
            .Select(t => new { t.Origin, t.Name })
            .ToListAsync(cancellationToken);
        foreach (var tool in tools)
        {
            foreach (var function in ToolNaming.FunctionsOf(tool.Origin, tool.Name))
            {
                if (string.Equals(function, slug, StringComparison.OrdinalIgnoreCase))
                {
                    return Taken(function);
                }
            }
        }

        return null;
    }

    private static Error Taken(string name) => Error.Conflict(
        "tool.name_taken",
        $"The name {name} is already taken: tools, lookup operations and agents share the names the model calls them by.");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$")]
    private static partial Regex NamePattern();
}
