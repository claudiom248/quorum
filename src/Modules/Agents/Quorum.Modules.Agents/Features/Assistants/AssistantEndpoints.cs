using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;

namespace Quorum.Modules.Agents.Features.Assistants;

/// <summary>The assistants of the tenant: what a conversation talks to, answering through a root agent (spec 013 D-2).</summary>
internal sealed class AssistantEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var assistants = app.MapGroup("/api/assistants").WithTags("Assistants");

        assistants.MapGet("/", ListAsync).RequireAuthorization().WithName("ListAssistants");
    }

    private static async Task<Ok<List<AssistantResponse>>> ListAsync(AgentsDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await (
            from assistant in db.Assistants
            join agent in db.Agents on assistant.RootAgentId equals agent.Id
            orderby assistant.Id
            select new AssistantResponse(assistant.Id, assistant.Name, agent.Slug, assistant.MaxParallelSubAgents))
            .AsNoTracking()
            .ToListAsync(cancellationToken));
}

internal sealed record AssistantResponse(Guid Id, string Name, string RootAgentSlug, int MaxParallelSubAgents);
