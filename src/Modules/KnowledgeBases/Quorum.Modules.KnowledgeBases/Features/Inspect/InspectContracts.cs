using System.Text.Json;

namespace Quorum.Modules.KnowledgeBases.Features.Inspect;

/// <summary>Where the knowledge base lives, as the administrator types it. <paramref name="VectorName"/> is only needed when the collection has several vectors.</summary>
internal sealed record ConnectionRequest(string? Url, string? ApiKey, string? Collection, string? VectorName);

/// <param name="Schema">A pasted JSON Schema of the payload (text, or the schema object itself); omitted, the payload is sampled.</param>
internal sealed record InspectRequest(string? Kind, ConnectionRequest? Connection, string? EmbeddingModel, string? QueryPrefix, JsonElement? Schema);

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

internal sealed record DraftMappingDto(RolesDto Roles, IReadOnlyList<DraftFieldDto> Fields);

internal sealed record RolesDto(string? Text, string? DocumentId, string? Title, string? Position, string? SourceUrl);

/// <param name="DictionaryFieldId">The visible dictionary field of that name, when there is one.</param>
/// <param name="Issues">Codes that block saving until resolved (collision, invalid or reserved name, type conflict with the dictionary).</param>
internal sealed record DraftFieldDto(string Path, string Name, string Type, Guid? DictionaryFieldId, IReadOnlyList<string> Issues);

/// <summary>A dictionary field the draft needs and that does not exist yet; saving creates it only when the administrator confirms.</summary>
internal sealed record ProposedFieldDto(string Name, string Type);

internal sealed record WarningDto(string Code, string Message, string? Path);
