using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Relations;

/// <param name="Connection">The members depend on the kind and leave out the source, which is what is being looked for.</param>
/// <param name="Filter">Keeps the relations whose name contains it, ignoring case.</param>
internal sealed record RelationsRequest(string? Kind, JsonElement? Connection, string? Filter);

internal sealed record RelationVectorDto(string Name, string Type, int? Dimensions);

internal sealed record RelationDto(string Schema, string Name, string Type, long? ApproximateRows, IReadOnlyList<RelationVectorDto> VectorColumns);

internal sealed record RelationsResponse(bool Reachable, IReadOnlyList<RelationDto> Relations, bool Truncated, IReadOnlyList<WarningDto> Warnings);

/// <summary>Lists the sources of a database that the connection can read and that hold a vector, for the administrator to pick from (spec 008 D-5).</summary>
internal sealed class RelationsEndpoint : IEndpointGroup
{
    /// <summary>More than this are not useful in a list a person picks from; the text filter narrows them (D-5).</summary>
    public const int MaxRelations = 200;

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/knowledge-bases")
            .WithTags("Knowledge bases")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage)
            .MapPost("/relations", ListAsync)
            .WithName("ListKnowledgeBaseRelations");
    }

    private static async Task<Results<Ok<RelationsResponse>, ProblemHttpResult>> ListAsync(
        RelationsRequest request,
        StoreAdapters adapters,
        CancellationToken cancellationToken)
    {
        if (!DictionaryFieldRules.TryParseMember<KnowledgeBaseKind>(request.Kind, out var kind) || !adapters.Kinds.Contains(kind))
        {
            return Error.Validation("knowledge_base.unsupported_kind", adapters.UnsupportedKindMessage()).ToProblem();
        }

        var adapter = adapters.For(kind);
        if (adapter.Capability<ISourceListingCapability>() is not { } listing)
        {
            return Error.Validation("knowledge_base.unsupported_operation", $"A {kind} knowledge base has no list of sources: name the source in the connection.").ToProblem();
        }

        var read = listing.ReadConnection(request.Connection);
        if (!read.IsSuccess)
        {
            return read.Error.ToProblem();
        }

        // Nothing is stored yet, so a connection that leaves its secret to a stored one cannot be resolved here.
        var kept = adapter.KeepSecrets(read.Value, stored: null);
        if (!kept.IsSuccess)
        {
            return kept.Error.ToProblem();
        }

        var listed = await listing.ListAsync(kept.Value, request.Filter, MaxRelations, cancellationToken);
        return TypedResults.Ok(new RelationsResponse(
            listed.Reachable,
            listed.Sources.Select(source => new RelationDto(
                source.Schema,
                source.Name,
                source.Type,
                source.ApproximateRows,
                source.Vectors.Select(vector => new RelationVectorDto(vector.Name, vector.Type, vector.Dimensions)).ToList())).ToList(),
            listed.Truncated,
            listed.Warnings.Select(warning => new WarningDto(warning.Code, warning.Message, warning.Path)).ToList()));
    }
}
