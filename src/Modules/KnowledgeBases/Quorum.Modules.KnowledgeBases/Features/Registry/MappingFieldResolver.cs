using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Dictionary;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Registry;

/// <summary>The metadata fields of a mapping, each bound to a dictionary field; <see cref="Created"/> are the ones the administrator confirmed and that do not exist yet.</summary>
internal sealed record ResolvedFields(List<MappedField> Fields, List<DictionaryField> Created, List<(string Path, DictionaryFieldType Type)> Types);

/// <summary>
/// Binds the mapped metadata fields to the dictionary: by id, or by name when the field exists or the administrator confirmed its
/// creation (D-10). Nothing is written: the caller saves the created fields together with the knowledge base.
/// </summary>
internal static class MappingFieldResolver
{
    private static readonly Error NotVisible = Error.Validation(
        "knowledge_base.field_not_visible",
        "A dictionary field of the mapping is not visible to everyone this knowledge base is for: a Global knowledge base can only use Global fields.");

    public static async Task<Result<ResolvedFields>> ResolveAsync(
        KnowledgeBasesDbContext db,
        Guid tenantId,
        KnowledgeBaseScope scope,
        IReadOnlyList<MappedFieldRequest> requested,
        IReadOnlyList<CreateFieldRequest> proposed,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var visible = await DictionaryVisibility.Fields(db).AsNoTracking().ToListAsync(cancellationToken);
        var fieldScope = scope == KnowledgeBaseScope.Global ? DictionaryFieldScope.Global : DictionaryFieldScope.Tenant;
        var result = new ResolvedFields([], [], []);
        var used = new HashSet<Guid>();

        foreach (var request in requested)
        {
            var path = request.Path!.Trim();
            var field = request.DictionaryFieldId is { } id
                ? visible.FirstOrDefault(f => f.Id == id)
                : result.Created.FirstOrDefault(f => Matches(f, request)) ?? visible.FirstOrDefault(f => Matches(f, request));

            if (request.DictionaryFieldId is not null && field is null)
            {
                return NotVisible;
            }

            if (field is null)
            {
                var created = await CreateAsync(db, tenantId, fieldScope, DictionaryFieldRules.NormalizeCluster(request.Cluster), request.Name?.Trim(), proposed, clock, cancellationToken);
                if (!created.IsSuccess)
                {
                    return created.Error;
                }

                field = created.Value;
                result.Created.Add(field);
            }
            else if (request.DictionaryFieldId is null
                && proposed.FirstOrDefault(p => p.Name?.Trim() == field.Name && DictionaryFieldRules.NormalizeCluster(p.Cluster) == field.Cluster) is { } proposal
                && DictionaryFieldRules.TryParseMember<DictionaryFieldType>(proposal.Type, out var proposedType)
                && proposedType != field.Type)
            {
                return Error.Conflict(
                    "knowledge_base.field_type_conflict",
                    $"The dictionary field '{field.Name}' is of type {field.Type}, not {proposedType}: map the payload field to it only if its values fit, or use another name.");
            }

            if (scope == KnowledgeBaseScope.Global && field.Scope != DictionaryFieldScope.Global)
            {
                return NotVisible;
            }

            if (!used.Add(field.Id))
            {
                return MappingValidation.InvalidMapping($"The dictionary field '{field.Name}' is bound to two payload fields: each field can be mapped once.");
            }

            if (request.Listable && field.Type is not (DictionaryFieldType.Text or DictionaryFieldType.TextList))
            {
                return MappingValidation.InvalidListableField(path, field.Type);
            }

            result.Fields.Add(new MappedField(path, field.Id, request.Listable));
            result.Types.Add((path, field.Type));
        }

        return result;
    }

    // A field is named by its cluster and its name: a request without a cluster means a field with none.
    private static bool Matches(DictionaryField field, MappedFieldRequest request) =>
        field.Name == request.Name?.Trim() && field.Cluster == DictionaryFieldRules.NormalizeCluster(request.Cluster);

    private static string Qualified(string? cluster, string? name) => cluster is null ? name ?? string.Empty : $"{cluster}/{name}";

    private static async Task<Result<DictionaryField>> CreateAsync(
        KnowledgeBasesDbContext db,
        Guid tenantId,
        DictionaryFieldScope scope,
        string? cluster,
        string? name,
        IReadOnlyList<CreateFieldRequest> proposed,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var proposal = proposed.FirstOrDefault(p => p.Name?.Trim() == name && DictionaryFieldRules.NormalizeCluster(p.Cluster) == cluster);
        if (name is null || proposal is null)
        {
            return MappingValidation.InvalidMapping(
                $"The dictionary field '{Qualified(cluster, name)}' does not exist. Confirm its creation (createFields) or map the payload field to an existing field.");
        }

        if (!DictionaryFieldRules.IsValidName(name))
        {
            return Error.Validation("knowledge_base.invalid_field_name", "A dictionary field name is 1 to 40 characters: lower-case letters, digits and '_', starting with a letter.");
        }

        if (!DictionaryFieldRules.IsValidCluster(cluster))
        {
            return Error.Validation("knowledge_base.invalid_field_cluster", "A cluster is 1 to 40 characters: lower-case letters, digits and '_', starting with a letter.");
        }

        if (DictionaryFieldRules.IsReservedName(name))
        {
            return Error.Validation("knowledge_base.reserved_field_name", "The name is reserved: it cannot be 'query' or 'knowledge_base' nor end with '_min', '_max', '_from' or '_to'.");
        }

        if (!DictionaryFieldRules.TryParseMember<DictionaryFieldType>(proposal.Type, out var type))
        {
            return Error.Validation("knowledge_base.invalid_field_type", "The type of a new field must be Text, Number, Boolean, Date or TextList.");
        }

        if (proposal.Description?.Length > DictionaryField.MaxDescriptionLength)
        {
            return Error.Validation("knowledge_base.invalid_field_description", $"The description can have up to {DictionaryField.MaxDescriptionLength} characters.");
        }

        // Another tenant's private field with this name does not show here, but a Global field is seen by everyone.
        if (await DictionaryVisibility.NameIsTakenAsync(db, cluster, name, scope, cancellationToken))
        {
            return Error.Conflict("knowledge_base.field_name_taken", "A field with this name already exists where this field would be visible.");
        }

        return DictionaryField.Create(tenantId, name, type, proposal.Description, scope, clock.GetUtcNow(), cluster);
    }
}
