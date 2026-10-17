using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBase.Domain;
using Quorum.Modules.KnowledgeBase.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.KnowledgeBase.Features.Dictionary;

/// <summary>The metadata dictionary: typed fields defined once and reused by knowledge bases and rag tools.</summary>
internal sealed class DictionaryEndpoints : IEndpointGroup
{
    private static readonly Error InvalidName = Error.Validation(
        "knowledge_base.invalid_field_name",
        $"The name must be 1 to {DictionaryField.MaxNameLength} characters: lower-case letters, digits and '_', starting with a letter.");

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
        KnowledgeBaseDbContext db,
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

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, KnowledgeBaseDbContext db, CancellationToken cancellationToken)
    {
        var field = await db.DictionaryFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
        {
            return TypedResults.NotFound();
        }

        db.DictionaryFields.Remove(field);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<DictionaryFieldDto>>> ListAsync(KnowledgeBaseDbContext db, CancellationToken cancellationToken)
    {
        var fields = await DictionaryVisibility.Fields(db).AsNoTracking().OrderBy(f => f.Name).ToListAsync(cancellationToken);
        return TypedResults.Ok(fields.Select(DictionaryFieldDto.From).ToList());
    }

    private static async Task<Results<Created<CreatedFieldDto>, ProblemHttpResult>> CreateAsync(
        DictionaryFieldRequest request,
        KnowledgeBaseDbContext db,
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

        if (await DictionaryVisibility.NameIsTakenAsync(db, request.Name!, scope, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var field = DictionaryField.Create(tenant.TenantId!.Value, request.Name!, type, request.Description, scope, clock.GetUtcNow());
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

internal sealed record DictionaryFieldRequest(string? Name, string? Type, string? Description, string? Scope);

/// <summary>Only the description is editable: name, type and scope are what knowledge bases and tools were built on.</summary>
internal sealed record DictionaryFieldUpdateRequest(string? Description);

internal sealed record CreatedFieldDto(Guid Id);

internal sealed record DictionaryFieldDto(Guid Id, string Name, string Type, string? Description, string Scope, int UsedByCount)
{
    // No knowledge base can use a field until the registry exists: the count joins in with it.
    public static DictionaryFieldDto From(DictionaryField field) =>
        new(field.Id, field.Name, field.Type.ToString(), field.Description, field.Scope.ToString(), 0);
}
