using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Modules.Agents.Features.Agents;

/// <summary>An edge of an agent being saved, with the child it names resolved.</summary>
internal sealed record ResolvedEdge(Agent Child, SubAgentFailurePolicy FailurePolicy);

/// <summary>
/// What an administrator sent for an agent, checked and normalized: <see cref="Errors"/> says what is wrong when it cannot be saved.
/// The rules are those of spec 013: slug (D-18), endpoint with tools (D-19), scope (D-20), edges (D-5, D-21, D-24, AC-26).
/// </summary>
internal sealed record AgentInput(
    Dictionary<string, string[]> Errors,
    string Slug,
    string DisplayName,
    string Description,
    string SystemPrompt,
    Guid? ModelEndpointId,
    AgentScope Scope,
    List<Guid> ScopeTenantIds,
    List<ResolvedEdge> Edges)
{
    public static ValidationProblem SlugTaken() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["slug"] = ["Another agent already has this slug."] });

    public static ValidationProblem SubAgentGone() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["subAgents"] = ["A sub-agent was deleted while saving. Reload and try again."] });

    /// <param name="existing">The agent being replaced; <see langword="null"/> when creating.</param>
    public static async Task<AgentInput> ValidateAsync(AgentRequest request, Agent? existing, AgentsDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);

        var errors = new Dictionary<string, string[]>();

        var slug = request.Slug?.Trim() ?? string.Empty;
        if (!AgentRules.IsValidSlug(slug))
        {
            errors["slug"] = [$"The slug must be 1 to {AgentRules.MaxSlugLength} characters: lower-case letters, digits and '-', starting with a letter or digit."];
        }
        else if (await db.Agents.AnyAsync(a => a.Slug == slug && a.Id != (existing != null ? existing.Id : Guid.Empty), cancellationToken))
        {
            errors["slug"] = ["Another agent already has this slug."];
        }

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > AgentRules.MaxDisplayNameLength)
        {
            errors["displayName"] = [$"The display name must be 1 to {AgentRules.MaxDisplayNameLength} characters."];
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length is 0 or > AgentRules.MaxDescriptionLength)
        {
            errors["description"] = [$"The description, which tells a calling agent when to use this one, must be 1 to {AgentRules.MaxDescriptionLength} characters."];
        }

        var systemPrompt = request.SystemPrompt?.Trim() ?? string.Empty;
        if (systemPrompt.Length == 0 || !ModelEndpointRules.IsValidSystemPrompt(systemPrompt))
        {
            errors["systemPrompt"] = [$"The system prompt is required and at most {AssistantDefaults.MaxSystemPromptLength} characters."];
        }

        var endpoint = request.ModelEndpointId is { } endpointId
            ? await db.ModelEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken)
            : null;
        if (request.ModelEndpointId is not null && endpoint is null)
        {
            errors["modelEndpointId"] = ["The model endpoint does not exist."];
        }

        var scope = AgentScope.Tenant;
        var tenantIds = (request.ScopeTenantIds ?? []).Distinct().ToList();
        if (!Enum.TryParse(request.Scope, ignoreCase: true, out scope) || !Enum.IsDefined(scope) || int.TryParse(request.Scope, out _))
        {
            errors["scope"] = ["The scope must be Global or Tenant."];
        }
        else if (scope == AgentScope.Global && tenantIds.Count > 0)
        {
            errors["scopeTenantIds"] = ["A global scope has no tenants."];
        }
        else if (tenantIds.Contains(Guid.Empty))
        {
            errors["scopeTenantIds"] = ["A tenant id in the scope is empty."];
        }

        var edges = await ResolveEdgesAsync(request.SubAgents, slug, existing, scope, tenantIds, errors, db, cancellationToken);

        // Spec 013 D-19: delegation and tools are function calls, which the model of the endpoint must support.
        if (endpoint is { SupportsTools: false } && !errors.ContainsKey("modelEndpointId")
            && (edges.Count > 0 || (existing is not null && await db.AgentTools.AnyAsync(t => t.AgentId == existing.Id, cancellationToken))))
        {
            errors["modelEndpointId"] = ["This agent has tools or sub-agents, and the model endpoint does not support tools. Choose another endpoint or remove them."];
        }

        return new AgentInput(errors, slug, displayName, description, systemPrompt, request.ModelEndpointId, scope, tenantIds, edges);
    }

    private static async Task<List<ResolvedEdge>> ResolveEdgesAsync(
        List<SubAgentDocument>? documents,
        string slug,
        Agent? existing,
        AgentScope scope,
        List<Guid> tenantIds,
        Dictionary<string, string[]> errors,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var wanted = new List<(string Slug, SubAgentFailurePolicy Policy)>();
        foreach (var document in documents ?? [])
        {
            var childSlug = document.Slug?.Trim() ?? string.Empty;
            if (!AgentRules.IsValidSlug(childSlug))
            {
                problems.Add($"'{childSlug}' is not a valid agent slug.");
            }
            else if (childSlug == slug)
            {
                problems.Add("An agent cannot be its own sub-agent.");
            }
            else if (wanted.Any(w => w.Slug == childSlug))
            {
                problems.Add($"'{childSlug}' is listed more than once.");
            }
            else if (string.IsNullOrWhiteSpace(document.FailurePolicy))
            {
                wanted.Add((childSlug, SubAgentFailurePolicy.ReturnError));
            }
            else if (Enum.TryParse<SubAgentFailurePolicy>(document.FailurePolicy, ignoreCase: true, out var policy) && Enum.IsDefined(policy) && !int.TryParse(document.FailurePolicy, out _))
            {
                wanted.Add((childSlug, policy));
            }
            else
            {
                problems.Add($"The failure policy of '{childSlug}' must be ReturnError or AbortTurn.");
            }
        }

        var slugs = wanted.Select(w => w.Slug).ToList();
        var children = await db.Agents.Where(a => slugs.Contains(a.Slug)).ToListAsync(cancellationToken);
        var edges = new List<ResolvedEdge>();
        foreach (var (childSlug, policy) in wanted)
        {
            if (children.FirstOrDefault(c => c.Slug == childSlug) is not { } child)
            {
                problems.Add($"There is no agent '{childSlug}'.");
            }
            else if (!AgentRules.IsVisibleAsWidelyAs(child.Scope, child.ScopeTenantIds, scope, tenantIds))
            {
                problems.Add($"'{childSlug}' is visible to fewer tenants than this agent: a sub-agent must be at least as widely visible as its parent.");
            }
            else
            {
                edges.Add(new ResolvedEdge(child, policy));
            }
        }

        if (existing is not null && edges.Count > 0)
        {
            var toolNames = await ToolNamesOfAsync(db, existing.Id, cancellationToken);
            problems.AddRange(edges
                .Where(e => AgentRules.ClashesWithTool(e.Child.Slug, toolNames))
                .Select(e => $"'{e.Child.Slug}' is also the name of a tool of this agent: the model would see two tools with one name."));
        }

        if (problems.Count > 0)
        {
            errors["subAgents"] = [.. problems];
        }

        return edges;
    }

    /// <summary>The names under which the model sees the tools active on an agent.</summary>
    public static async Task<List<string>> ToolNamesOfAsync(AgentsDbContext db, Guid agentId, CancellationToken cancellationToken)
    {
        var active = db.AgentTools.Where(a => a.AgentId == agentId).Select(a => a.ToolId);
        var pool = await ToolPool.For(db, agentId).AsNoTracking().Where(t => active.Contains(t.Tool.Id)).ToListAsync(cancellationToken);
        return [.. pool.Select(t => t.ExposedName)];
    }
}
