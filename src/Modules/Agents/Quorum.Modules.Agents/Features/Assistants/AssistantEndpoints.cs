using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Assistants;

/// <summary>The assistants of the tenant: what a conversation talks to, answering through a root agent (spec 013 D-2, D-34).</summary>
internal sealed class AssistantEndpoints : IEndpointGroup
{
    private static readonly Error NotFound = Error.NotFound("assistant.not_found", "The assistant does not exist.");

    private static readonly Error DefaultNotExposed = Error.Conflict(
        "assistant.default_not_exposed",
        "The first assistant stays exposed: the first-run setup answers through it, and consumers can always start with it.");

    public void Map(IEndpointRouteBuilder app)
    {
        var assistants = app.MapGroup("/api/assistants").WithTags("Assistants");

        assistants.MapGet("/", ListAsync).RequireAuthorization().WithName("ListAssistants");
        assistants.MapGet("/{id:guid}", GetAsync).RequireAuthorization().WithName("GetAssistant");
        assistants.MapPost("/", CreateAsync).RequireAuthorization(QuorumPolicies.AgentsManage).WithName("CreateAssistant");
        assistants.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(QuorumPolicies.AgentsManage).WithName("UpdateAssistant");
        assistants.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(QuorumPolicies.AgentsManage).WithName("DeleteAssistant");
    }

    private static async Task<Ok<List<AssistantResponse>>> ListAsync(AgentsDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await (
            from assistant in db.Assistants
            join agent in db.Agents on assistant.RootAgentId equals agent.Id
            orderby assistant.Id
            select new AssistantResponse(assistant.Id, assistant.Name, agent.Slug, assistant.MaxParallelSubAgents, assistant.Exposed, assistant.AllowToolSelection))
            .AsNoTracking()
            .ToListAsync(cancellationToken));

    private static async Task<Results<Ok<AssistantResponse>, ProblemHttpResult>> GetAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        var assistant = await (
            from a in db.Assistants
            join agent in db.Agents on a.RootAgentId equals agent.Id
            where a.Id == id
            select new AssistantResponse(a.Id, a.Name, agent.Slug, a.MaxParallelSubAgents, a.Exposed, a.AllowToolSelection))
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        return assistant is null ? NotFound.ToProblem() : TypedResults.Ok(assistant);
    }

    private static async Task<Results<CreatedAtRoute<AssistantResponse>, ValidationProblem>> CreateAsync(
        AssistantRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var input = await AssistantInput.ValidateAsync(request, db, cancellationToken);
        if (input.Errors.Count > 0)
        {
            return TypedResults.ValidationProblem(input.Errors);
        }

        var assistant = Assistant.Create(tenantId, input.Name, input.Root.Id, input.MaxParallelSubAgents, clock.GetUtcNow());
        assistant.SetChatExposure(request.Exposed ?? false, request.AllowToolSelection ?? false);
        db.Assistants.Add(assistant);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.CreatedAtRoute(
            new AssistantResponse(assistant.Id, assistant.Name, input.Root.Slug, assistant.MaxParallelSubAgents, assistant.Exposed, assistant.AllowToolSelection),
            "GetAssistant",
            new RouteValueDictionary { ["id"] = assistant.Id });
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id,
        AssistantRequest request,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        if (await db.Assistants.SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } assistant)
        {
            return NotFound.ToProblem();
        }

        var input = await AssistantInput.ValidateAsync(request, db, cancellationToken, assistant.MaxParallelSubAgents);
        if (input.Errors.Count > 0)
        {
            return TypedResults.ValidationProblem(input.Errors);
        }

        var exposed = request.Exposed ?? assistant.Exposed;
        if (!exposed && await IsDefaultAsync(assistant, db, cancellationToken))
        {
            return DefaultNotExposed.ToProblem();
        }

        assistant.Revise(input.Name, input.Root.Id, input.MaxParallelSubAgents);
        assistant.SetChatExposure(exposed, request.AllowToolSelection ?? assistant.AllowToolSelection);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The first-run assistant (spec 013 D-35): the oldest one, the one the setup and the settings facade answer through.</summary>
    private static async Task<bool> IsDefaultAsync(Assistant assistant, AgentsDbContext db, CancellationToken cancellationToken) =>
        await db.Assistants.OrderBy(a => a.Id).Select(a => a.Id).FirstAsync(cancellationToken) == assistant.Id;

    /// <summary>
    /// Allowed with conversations (spec 013 D-36): they keep their soft <c>AssistantId</c>, stay readable, and sending to one is refused
    /// by the chat as for any assistant that does not exist.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Assistants.SingleOrDefaultAsync(a => a.Id == id, cancellationToken) is not { } assistant)
        {
            return NotFound.ToProblem();
        }

        // Conversations are created on the first assistant; with none left they could not be started.
        if (!await db.Assistants.AnyAsync(a => a.Id != id, cancellationToken))
        {
            return Error.Conflict("assistant.last", "The last assistant cannot be deleted: create another one first.").ToProblem();
        }

        db.Assistants.Remove(assistant);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

internal sealed record AssistantResponse(Guid Id, string Name, string RootAgentSlug, int MaxParallelSubAgents, bool Exposed, bool AllowToolSelection);

/// <summary>Exposure and tool selection left out keep the stored value on update (spec 018 D-1, D-2).</summary>
internal sealed record AssistantRequest(string? Name, string? RootAgentSlug, int? MaxParallelSubAgents, bool? Exposed = null, bool? AllowToolSelection = null);

/// <summary>What an administrator sent for an assistant, checked: the root agent must exist, be visible and have an endpoint (spec 013 D-12, D-25).</summary>
internal sealed record AssistantInput(Dictionary<string, string[]> Errors, string Name, Agent Root, int MaxParallelSubAgents)
{
    public const int MaxNameLength = 200;

    /// <summary>Above this the limit stops being a limit: a model asking for more sub-agents than this is out of control, not busy.</summary>
    public const int MaxParallelLimit = 32;

    public static async Task<AssistantInput> ValidateAsync(AssistantRequest request,
        AgentsDbContext db,
        CancellationToken cancellationToken,
        int defaultMaxParallelSubAgents = Assistant.DefaultMaxParallelSubAgents)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);

        var errors = new Dictionary<string, string[]>();

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"The name must be 1 to {MaxNameLength} characters."];
        }

        var slug = request.RootAgentSlug?.Trim() ?? string.Empty;
        var root = slug.Length == 0 ? null : await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Slug == slug, cancellationToken);
        if (root is null)
        {
            errors["rootAgentSlug"] = ["There is no agent with this slug."];
        }
        else if (root.ModelEndpointId is null)
        {
            errors["rootAgentSlug"] = ["The root agent needs a model endpoint of its own: a sub-agent without one inherits the root's."];
        }

        var parallel = request.MaxParallelSubAgents ?? defaultMaxParallelSubAgents;
        if (parallel is < 1 or > MaxParallelLimit)
        {
            errors["maxParallelSubAgents"] = [$"The limit must be between 1 and {MaxParallelLimit}."];
        }

        return new AssistantInput(errors, name, root!, parallel);
    }
}
