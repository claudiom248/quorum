using System.Text.Json.Serialization;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Inspect;

namespace Quorum.Modules.KnowledgeBases.Features.Registry;

/// <param name="Connection">An omitted <c>ApiKey</c> keeps the stored one on an update.</param>
/// <param name="CreateFields">The dictionary fields the mapping names that do not exist yet; they are created only when listed here (D-10).</param>
internal sealed record KnowledgeBaseRequest(
    string? Name,
    string? Description,
    string? Kind,
    ConnectionRequest? Connection,
    string? EmbeddingModel,
    string? QueryPrefix,
    MappingRequest? Mapping,
    ScopeRequest? Scope,
    List<CreateFieldRequest>? CreateFields);

internal sealed record MappingRequest(RolesDto? Roles, List<MappedFieldRequest>? Fields);

/// <param name="DictionaryFieldId">The dictionary field, when the client knows it; otherwise <paramref name="Name"/> finds it, or names the one to create.</param>
/// <param name="Cluster">With <paramref name="Name"/>: the cluster of the field; none means a field with no cluster.</param>
internal sealed record MappedFieldRequest(string? Path, Guid? DictionaryFieldId, string? Name, string? Cluster = null);

internal sealed record CreateFieldRequest(string? Name, string? Type, string? Description, string? Cluster = null);

internal sealed record ScopeRequest(string? Kind, List<Guid>? TenantIds, List<Guid>? AssistantIds);

internal sealed record RegisteredResponse(Guid Id, string Status, IReadOnlyList<WarningDto> Warnings);

internal sealed record VerificationResponse(string Status, IReadOnlyList<WarningDto> Warnings);

/// <summary>One row of the list. A knowledge base of another tenant is <see cref="Shared"/> and shows name, description and status only.</summary>
internal sealed record KnowledgeBaseListItem(
    Guid Id,
    string Name,
    string Description,
    string Status,
    bool Shared,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastVerifiedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Warning)
{
    public static KnowledgeBaseListItem From(KnowledgeBase registration, Guid? currentTenantId) =>
        registration.TenantId == currentTenantId
            ? new(
                registration.Id,
                registration.Name,
                registration.Description,
                registration.Status.ToString(),
                Shared: false,
                registration.Kind.ToString(),
                registration.Scope.ToString(),
                registration.LastVerifiedAt,
                registration.LastError)
            : new(registration.Id, registration.Name, registration.Description, registration.Status.ToString(), Shared: true, null, null, null, null);
}

/// <param name="HasApiKey">Whether a key is stored. The key itself is never returned.</param>
internal sealed record ConnectionResponse(string Url, string Collection, string? VectorName, bool HasApiKey);

internal sealed record MappedFieldResponse(string Path, Guid DictionaryFieldId, string? Name, string? Cluster = null);

internal sealed record MappingResponse(RolesDto Roles, IReadOnlyList<MappedFieldResponse> Fields);

internal sealed record ScopeResponse(string Kind, IReadOnlyList<Guid> TenantIds, IReadOnlyList<Guid> AssistantIds);

/// <summary>The detail of a knowledge base. Of another tenant's, only the members that are not ignorable: name, description, status.</summary>
internal sealed record KnowledgeBaseDetail(
    Guid Id,
    string Name,
    string Description,
    string Status,
    bool Shared,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConnectionResponse? Connection,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EmbeddingModel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QueryPrefix,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MappingResponse? Mapping,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScopeResponse? Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Warning,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastVerifiedAt)
{
    public static KnowledgeBaseDetail Shareable(KnowledgeBase registration) =>
        new(registration.Id, registration.Name, registration.Description, registration.Status.ToString(), Shared: true, null, null, null, null, null, null, null, null);

    public static KnowledgeBaseDetail Owned(KnowledgeBase registration, IReadOnlyDictionary<Guid, (string Name, string? Cluster)> fieldNames) => new(
        registration.Id,
        registration.Name,
        registration.Description,
        registration.Status.ToString(),
        Shared: false,
        registration.Kind.ToString(),
        new ConnectionResponse(registration.Connection.Url, registration.Connection.Collection, registration.Connection.VectorName, registration.Connection.ProtectedApiKey is not null),
        registration.EmbeddingModel,
        registration.QueryPrefix,
        new MappingResponse(
            new RolesDto(registration.Mapping.Roles.Text, registration.Mapping.Roles.DocumentId, registration.Mapping.Roles.Title, registration.Mapping.Roles.Position, registration.Mapping.Roles.SourceUrl),
            [.. registration.Mapping.Fields.Select(f => new MappedFieldResponse(f.Path, f.DictionaryFieldId, fieldNames.TryGetValue(f.DictionaryFieldId, out var label) ? label.Name : null, fieldNames.TryGetValue(f.DictionaryFieldId, out var clustered) ? clustered.Cluster : null))]),
        new ScopeResponse(registration.Scope.ToString(), registration.ScopeTenantIds, registration.ScopeAssistantIds),
        registration.LastError,
        registration.LastVerifiedAt);
}
