using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Inspect;

namespace Quorum.Modules.KnowledgeBases.Features.Registry;

/// <param name="Connection">The members depend on the kind; the adapter of the kind reads them. An omitted secret keeps the stored one on an update, under the kind's rule.</param>
/// <param name="CreateFields">The dictionary fields the mapping names that do not exist yet; they are created only when listed here (D-10).</param>
internal sealed record KnowledgeBaseRequest(
    string? Name,
    string? Description,
    string? Kind,
    JsonElement? Connection,
    string? EmbeddingModel,
    string? QueryPrefix,
    MappingRequest? Mapping,
    ScopeRequest? Scope,
    List<CreateFieldRequest>? CreateFields,
    int? LookupRefreshHours = null);

/// <param name="CountedPoint">What a lookup counts (spec 007 D-2). Omitted means not set: a mapping is saved whole.</param>
/// <param name="EntityName">The path of the name of an entity, for a knowledge base whose points are entities (spec 007 D-4).</param>
internal sealed record MappingRequest(RolesDto? Roles, List<MappedFieldRequest>? Fields, CountedPointRequest? CountedPoint = null, string? EntityName = null);

/// <param name="Value"><c>true</c>, a number or a text: it has to fit the type of the field at <paramref name="Path"/>.</param>
internal sealed record CountedPointRequest(string? Path, JsonElement? Value);

internal sealed record CountedPointResponse(string Path, JsonElement Value);

/// <param name="DictionaryFieldId">The dictionary field, when the client knows it; otherwise <paramref name="Name"/> finds it, or names the one to create.</param>
/// <param name="Cluster">With <paramref name="Name"/>: the cluster of the field; none means a field with no cluster.</param>
/// <param name="Listable">Whether the distinct values of the field are kept as a lookup (spec 007 D-3).</param>
/// <param name="EntityAttribute">Whether the entity lookup lists the field beside the name of each entity (spec 007 D-4); at most 10 fields.</param>
internal sealed record MappedFieldRequest(string? Path, Guid? DictionaryFieldId, string? Name, string? Cluster = null, bool Listable = false, bool EntityAttribute = false);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScopeResponse? Scope,
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
                new ScopeResponse(registration.Scope.ToString(), registration.ScopeTenantIds, registration.ScopeAssistantIds),
                registration.LastVerifiedAt,
                registration.LastError)
            : new(registration.Id, registration.Name, registration.Description, registration.Status.ToString(), Shared: true, null, new ScopeResponse(registration.Scope.ToString(), [], []), null, null);
}


/// <param name="FilterName">The name a test search filters this field by, which is not always its name (spec 003 A1-6).</param>
internal sealed record MappedFieldResponse(string Path, Guid DictionaryFieldId, string? Name, string? Cluster = null, string? FilterName = null, bool Listable = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool EntityAttribute = false);

internal sealed record MappingResponse(
    RolesDto Roles,
    IReadOnlyList<MappedFieldResponse> Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CountedPointResponse? CountedPoint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntityName = null);

internal sealed record ScopeResponse(string Kind, IReadOnlyList<Guid> TenantIds, IReadOnlyList<Guid> AssistantIds);

/// <summary>The detail of a knowledge base. Of another tenant's, only the members that are not ignorable: name, description, status.</summary>
internal sealed record KnowledgeBaseDetail(
    Guid Id,
    string Name,
    string Description,
    string Status,
    bool Shared,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Connection,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EmbeddingModel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QueryPrefix,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MappingResponse? Mapping,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScopeResponse? Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Warning,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastVerifiedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LookupRefreshHours = null)
{
    public static KnowledgeBaseDetail Shareable(KnowledgeBase registration) =>
        new(registration.Id, registration.Name, registration.Description, registration.Status.ToString(), Shared: true, null, null, null, null, null, null, null, null);

    public static KnowledgeBaseDetail Owned(KnowledgeBase registration, JsonObject connection, IReadOnlyDictionary<Guid, (string Name, string? Cluster)> fieldNames, IReadOnlyDictionary<Guid, string> filterNames) => new(
        registration.Id,
        registration.Name,
        registration.Description,
        registration.Status.ToString(),
        Shared: false,
        registration.Kind.ToString(),
        connection,
        registration.EmbeddingModel,
        registration.QueryPrefix,
        new MappingResponse(
            new RolesDto(registration.Mapping.Roles.Text, registration.Mapping.Roles.DocumentId, registration.Mapping.Roles.Title, registration.Mapping.Roles.Position, registration.Mapping.Roles.SourceUrl),
            [.. registration.Mapping.Fields.Select(f => new MappedFieldResponse(f.Path, f.DictionaryFieldId, fieldNames.TryGetValue(f.DictionaryFieldId, out var label) ? label.Name : null, fieldNames.TryGetValue(f.DictionaryFieldId, out var clustered) ? clustered.Cluster : null, filterNames.GetValueOrDefault(f.DictionaryFieldId), f.Listable, f.EntityAttribute))],
            registration.Mapping.CountedPoint is { } counted ? new CountedPointResponse(counted.Path, counted.ToJsonElement()) : null,
            registration.Mapping.EntityName),
        new ScopeResponse(registration.Scope.ToString(), registration.ScopeTenantIds, registration.ScopeAssistantIds),
        registration.LastError,
        registration.LastVerifiedAt,
        registration.LookupRefreshHours);
}
