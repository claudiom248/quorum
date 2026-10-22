using System.Text.Json;
using System.Text.Json.Serialization;
using Quorum.Modules.KnowledgeBases.Features.Registry;

namespace Quorum.Modules.KnowledgeBases.Features.Inspect;

/// <param name="Connection">The members depend on the kind; the adapter of the kind reads them.</param>
/// <param name="Schema">A pasted JSON Schema of the payload (text, or the schema object itself); omitted, the payload is sampled.</param>
internal sealed record InspectRequest(string? Kind, JsonElement? Connection, string? EmbeddingModel, string? QueryPrefix, JsonElement? Schema);

internal sealed record InspectResponse(
    bool Reachable,
    bool CollectionExists,
    IReadOnlyList<VectorDto> Vectors,
    int? EmbeddingSize,
    bool? DimensionMatch,
    DraftMappingDto DraftMapping,
    IReadOnlyList<ProposedFieldDto> ProposedFields,
    IReadOnlyList<string> UnindexedFields,
    IReadOnlyList<WarningDto> Warnings);

internal sealed record VectorDto(string? Name, int Size, string Distance);

/// <param name="CountedPoint">The point of each document the lookups count, when a field of the payload looks like one (spec 007 D-19).</param>
internal sealed record DraftMappingDto(
    RolesDto Roles,
    IReadOnlyList<DraftFieldDto> Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CountedPointResponse? CountedPoint = null);

internal sealed record RolesDto(string? Text, string? DocumentId, string? Title, string? Position, string? SourceUrl);

/// <param name="DictionaryFieldId">The visible dictionary field of that name, when there is one.</param>
/// <param name="Issues">Codes that block saving until resolved (collision, invalid or reserved name, type conflict with the dictionary).</param>
/// <param name="Cluster">The cluster of the preselected field.</param>
/// <param name="Candidates">Several visible fields have this name, in different clusters, so none is preselected: the administrator picks one.</param>
internal sealed record DraftFieldDto(string Path, string Name, string Type, Guid? DictionaryFieldId, IReadOnlyList<string> Issues, string? Cluster = null, IReadOnlyList<FieldCandidateDto>? Candidates = null);

internal sealed record FieldCandidateDto(Guid Id, string? Cluster, string Type);

/// <summary>A dictionary field the draft needs and that does not exist yet; saving creates it only when the administrator confirms.</summary>
internal sealed record ProposedFieldDto(string Name, string Type);

internal sealed record WarningDto(string Code, string Message, string? Path);
