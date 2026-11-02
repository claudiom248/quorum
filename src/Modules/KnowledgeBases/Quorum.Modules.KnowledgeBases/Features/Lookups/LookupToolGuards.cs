using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// What a change of a knowledge base or a lookup must not do to the lookup tools that use it (spec 012 A1-4, A1-9): a tool keeps one name for
/// each target, and a role a tool relies on stays. Every tenant's tools are checked, because a global lookup or knowledge base is used by tools
/// its owner cannot see; the message names only the caller's own tools.
/// </summary>
internal static class LookupToolGuards
{
    /// <summary>
    /// The 409 for a mapping that removes a role a tool relies on, or for a rename that would give two targets of a tool one name; <see langword="null"/>
    /// when the change is allowed.
    /// </summary>
    public static async Task<Error?> KnowledgeBaseAsync(
        KnowledgeBasesDbContext db,
        ILookupToolUsage lookupTools,
        KnowledgeBase registration,
        string newName,
        KnowledgeBaseMapping newMapping,
        CancellationToken cancellationToken)
    {
        var before = registration.Mapping;
        var losesDocuments = IsDocument(before) && !IsDocument(newMapping);
        var losesEntities = before.EntityName is not null && newMapping.EntityName is null;

        if (losesDocuments || losesEntities)
        {
            List<Guid> entityLookups = losesEntities
                ? await db.Lookups.IgnoreQueryFilters().Where(l => l.KnowledgeBaseId == registration.Id && l.Kind == LookupKind.Entity).Select(l => l.Id).ToListAsync(cancellationToken)
                : [];
            var users = (await lookupTools.ListAsync(cancellationToken))
                .Where(t => (losesDocuments && t.KnowledgeBaseIds.Contains(registration.Id)) || (losesEntities && t.LookupIds.Any(entityLookups.Contains)))
                .ToList();
            if (users.Count > 0)
            {
                return InUse("knowledge_base.role_in_use", "The role this change removes is relied on by lookup tools", users, db.CurrentTenantId);
            }
        }

        return newName == registration.Name
            ? null
            : await TargetNameAsync(db, lookupTools, t => t.KnowledgeBaseIds.Contains(registration.Id), null, registration.Id, newName, cancellationToken);
    }

    /// <summary>The 409 for a rename that would give a target of a tool, the lookup, the name another target of that tool has.</summary>
    public static async Task<Error?> LookupRenameAsync(KnowledgeBasesDbContext db, ILookupToolUsage lookupTools, Lookup lookup, string newName, CancellationToken cancellationToken)
    {
        if (newName == lookup.Name)
        {
            return null;
        }

        return await TargetNameAsync(db, lookupTools, t => t.LookupIds.Contains(lookup.Id), lookup.Id, null, newName, cancellationToken);
    }

    private static bool IsDocument(KnowledgeBaseMapping mapping) => mapping.Roles.Text is not null || mapping.Roles.DocumentId is not null;

    private static async Task<Error?> TargetNameAsync(
        KnowledgeBasesDbContext db,
        ILookupToolUsage lookupTools,
        Func<LookupToolUse, bool> affected,
        Guid? renamedLookup,
        Guid? renamedKnowledgeBase,
        string newName,
        CancellationToken cancellationToken)
    {
        var tools = (await lookupTools.ListAsync(cancellationToken)).Where(affected).ToList();
        if (tools.Count == 0)
        {
            return null;
        }

        var lookupIds = tools.SelectMany(t => t.LookupIds).Distinct().ToList();
        var lookupNames = await db.Lookups.IgnoreQueryFilters().Where(l => lookupIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, cancellationToken);
        var knowledgeBaseIds = tools.SelectMany(t => t.KnowledgeBaseIds).Distinct().ToList();
        var knowledgeBaseNames = await db.KnowledgeBases.IgnoreQueryFilters().Where(k => knowledgeBaseIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, k => k.Name, cancellationToken);

        // The names as they would be once the change is saved: the renamed target carries the new one.
        if (renamedLookup is { } lookupId)
        {
            lookupNames[lookupId] = newName;
        }

        if (renamedKnowledgeBase is { } knowledgeBaseId)
        {
            knowledgeBaseNames[knowledgeBaseId] = newName;
        }

        var clashing = tools.Where(t => HasClash(t, lookupNames, knowledgeBaseNames)).ToList();
        return clashing.Count == 0
            ? null
            : InUse("lookup_tool.target_name_taken", $"The name '{newName}' would be shared by two targets of lookup tools", clashing, db.CurrentTenantId);
    }

    /// <summary>A target is its lookup's alias when it has one, else the lookup's name; a live knowledge base is known by its name.</summary>
    private static bool HasClash(LookupToolUse tool, Dictionary<Guid, string> lookupNames, Dictionary<Guid, string> knowledgeBaseNames)
    {
        var names = new List<string>();
        foreach (var id in tool.LookupIds)
        {
            if (tool.LookupAliases.GetValueOrDefault(id) is { } alias)
            {
                names.Add(alias);
            }
            else if (lookupNames.TryGetValue(id, out var name))
            {
                names.Add(name);
            }
        }

        foreach (var id in tool.KnowledgeBaseIds)
        {
            if (knowledgeBaseNames.TryGetValue(id, out var name))
            {
                names.Add(name);
            }
        }

        return names.GroupBy(n => n, StringComparer.Ordinal).Any(g => g.Count() > 1);
    }

    private static Error InUse(string code, string reason, IReadOnlyList<LookupToolUse> tools, Guid? callerTenant)
    {
        var own = tools.Where(t => t.TenantId == callerTenant).Select(t => t.ToolName).Order(StringComparer.Ordinal).ToList();
        var message = own.Count > 0
            ? $"{reason}: {string.Join(", ", own)}. Change those tools first."
            : $"{reason} of another tenant. Ask the owner of those tools to change them first.";
        return Error.Conflict(code, message);
    }
}
