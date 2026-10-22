using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.KnowledgeBases.Features.Dictionary;

/// <summary>The metadata dictionary: typed fields defined once and reused by knowledge bases and rag tools.</summary>
internal sealed class DictionaryEndpoints : IEndpointGroup
{
    private static readonly Error InvalidName = Error.Validation(
        "knowledge_base.invalid_field_name",
        $"The name must be 1 to {DictionaryField.MaxNameLength} characters: lower-case letters, digits and '_', starting with a letter.");

    private static readonly Error InvalidCluster = Error.Validation(
        "knowledge_base.invalid_field_cluster",
        $"The cluster is optional; when given it is 1 to {DictionaryField.MaxClusterLength} characters: lower-case letters, digits and '_', starting with a letter.");

    private static readonly Error ReservedName = Error.Validation(
        "knowledge_base.reserved_field_name",
        "The name is reserved: it cannot be 'query' or 'knowledge_base' nor end with '_min', '_max', '_from' or '_to', because tools use them for their own parameters.");

    private static readonly Error NameTaken = Error.Conflict(
        "knowledge_base.field_name_taken",
        "A field with this name already exists where this field would be visible.");

    private static readonly Error InvalidType = Error.Validation(
        "knowledge_base.invalid_field_type",
        "The type must be Text, Number, Boolean, Date or TextList.");

    private static readonly Error InvalidScope = Error.Validation(
        "knowledge_base.invalid_field_scope",
        "The scope must be Global or Tenant.");

    private static readonly Error InvalidDescription = Error.Validation(
        "knowledge_base.invalid_field_description",
        $"The description can have up to {DictionaryField.MaxDescriptionLength} characters.");

    public void Map(IEndpointRouteBuilder app)
    {
        var dictionary = app.MapGroup("/api/knowledge-bases/dictionary")
            .WithTags("Knowledge bases")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage);

        dictionary.MapGet("/", ListAsync).WithName("ListDictionaryFields");
        dictionary.MapPost("/", CreateAsync).WithName("CreateDictionaryField");
        dictionary.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateDictionaryField");
        dictionary.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteDictionaryField");
    }

    // Both look the field up with the tenant filter on: a field shared by another tenant is visible, never writable, and reads as missing.
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id,
        DictionaryFieldUpdateRequest request,
        KnowledgeBasesDbContext db,
        CancellationToken cancellationToken)
    {
        if (request.Description?.Length > DictionaryField.MaxDescriptionLength)
        {
            return InvalidDescription.ToProblem();
        }

        var field = await db.DictionaryFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
        {
            return TypedResults.NotFound();
        }

        field.Describe(request.Description);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(Guid id, KnowledgeBasesDbContext db, IRagToolUsage ragTools, ILookupToolUsage lookupTools, CancellationToken cancellationToken)
    {
        // The usage is read under the lock a knowledge base takes to save its mapping: one that was saved a moment ago is seen here,
        // and one that comes next finds the field gone. Without it both could succeed, leaving a mapping that names a deleted field.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DictionaryLock.AcquireAsync(db, cancellationToken);

        var field = await db.DictionaryFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
        {
            return TypedResults.NotFound();
        }

        var (bases, tools, lookups, others) = (await DictionaryUsage.LoadAsync(db, ragTools, lookupTools, cancellationToken)).UsersOf(id, db.CurrentTenantId);
        if (bases.Count + tools.Count + lookups.Count + others > 0)
        {
            return FieldInUse(bases, tools, lookups, others).ToProblem();
        }

        db.DictionaryFields.Remove(field);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // What other tenants have is counted, never named.
    private static Error FieldInUse(List<string> bases, List<string> tools, List<string> lookups, int others)
    {
        var parts = new List<string>();
        if (bases.Count > 0)
        {
            parts.Add($"knowledge bases {string.Join(", ", bases)}");
        }

        if (tools.Count > 0)
        {
            parts.Add($"rag tools {string.Join(", ", tools)}");
        }

        if (lookups.Count > 0)
        {
            parts.Add($"lookup tools {string.Join(", ", lookups)}");
        }

        var users = parts.Count > 0 ? string.Join(" and ", parts) : "knowledge bases or tools";
        var more = parts.Count > 0 && others > 0 ? $" and {others} of other tenants" : others > 0 ? $" ({others} of other tenants)" : string.Empty;
        return Error.Conflict("knowledge_base.field_in_use", $"The field is used by {users}{more}: remove it from their mapping or configuration first.");
    }

    private static async Task<Ok<List<DictionaryFieldDto>>> ListAsync(KnowledgeBasesDbContext db, IRagToolUsage ragTools, ILookupToolUsage lookupTools, CancellationToken cancellationToken)
    {
        var fields = await DictionaryVisibility.Fields(db).AsNoTracking().OrderBy(f => f.Cluster != null).ThenBy(f => f.Cluster).ThenBy(f => f.Name).ToListAsync(cancellationToken);
        var usage = await DictionaryUsage.LoadAsync(db, ragTools, lookupTools, cancellationToken);
        return TypedResults.Ok(fields.Select(field => DictionaryFieldDto.From(field, usage.CountFor(field.Id), db.CurrentTenantId)).ToList());
    }

    private static async Task<Results<Created<CreatedFieldDto>, ProblemHttpResult>> CreateAsync(
        DictionaryFieldRequest request,
        KnowledgeBasesDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!DictionaryFieldRules.IsValidName(request.Name))
        {
            return InvalidName.ToProblem();
        }

        if (DictionaryFieldRules.IsReservedName(request.Name!))
        {
            return ReservedName.ToProblem();
        }

        var cluster = DictionaryFieldRules.NormalizeCluster(request.Cluster);
        if (!DictionaryFieldRules.IsValidCluster(cluster))
        {
            return InvalidCluster.ToProblem();
        }

        if (!DictionaryFieldRules.TryParseMember<DictionaryFieldType>(request.Type, out var type))
        {
            return InvalidType.ToProblem();
        }

        if (!DictionaryFieldRules.TryParseMember<DictionaryFieldScope>(request.Scope, out var scope))
        {
            return InvalidScope.ToProblem();
        }

        if (request.Description?.Length > DictionaryField.MaxDescriptionLength)
        {
            return InvalidDescription.ToProblem();
        }

        if (await DictionaryVisibility.NameIsTakenAsync(db, cluster, request.Name!, scope, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var field = DictionaryField.Create(tenant.TenantId!.Value, request.Name!, type, request.Description, scope, clock.GetUtcNow(), cluster);
        db.DictionaryFields.Add(field);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            // A concurrent request took the name between the check and the insert.
            return NameTaken.ToProblem();
        }

        // No Location: a single field has no route to be read from.
        return TypedResults.Created((string?)null, new CreatedFieldDto(field.Id));
    }
}

/// <param name="Cluster">Optional: a label that groups related fields. With the name it identifies the field.</param>
internal sealed record DictionaryFieldRequest(string? Name, string? Type, string? Description, string? Scope, string? Cluster = null);

/// <summary>Only the description is editable: name, type and scope are what knowledge bases and tools were built on.</summary>
internal sealed record DictionaryFieldUpdateRequest(string? Description);

internal sealed record CreatedFieldDto(Guid Id);

/// <param name="Shared"><see langword="true"/> for a Global field of another tenant: it can be used but not changed or deleted.</param>
internal sealed record DictionaryFieldDto(Guid Id, string Name, string Type, string? Description, string Scope, int UsedByCount, string? Cluster = null, bool Shared = false)
{
    public static DictionaryFieldDto From(DictionaryField field, int usedByCount, Guid? currentTenantId = null) =>
        new(field.Id, field.Name, field.Type.ToString(), field.Description, field.Scope.ToString(), usedByCount, field.Cluster, currentTenantId is not null && field.TenantId != currentTenantId);
}
