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

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>What the administrator sends to change a lookup of any kind (spec 012 API contract). Every field is optional.</summary>
internal sealed record LookupEditRequest(string? Name, string? Description, string? Storage, List<LookupAttributeRequest>? Attributes);

/// <summary>
/// Edits the name, the description, the attributes and the storage mode of a lookup (spec 012 D-2, D-3, D-4).
/// </summary>
internal sealed class LookupEditEndpoints : IEndpointGroup
{
    private static readonly Error InvalidName = Error.Validation("lookup.invalid_name", $"A name is 1 to {Lookup.MaxNameLength} characters, without line breaks.");

    private static readonly Error InvalidDescription = Error.Validation(
        "lookup.invalid_description",
        $"A description is 1 to {Lookup.MaxDescriptionLength} characters.");

    private static readonly Error StorageNotSupported = Error.Validation("lookup.storage_not_supported", "This lookup cannot use that storage: only an entity lookup can be read from the store.");

    private static readonly Error NameTaken = Error.Conflict("lookup.name_taken", "Another lookup of this tenant already has this name.");

    private static readonly Error AttributeNotMapped = Error.Validation("lookup.invalid_attribute", "An attribute must be a field the knowledge base maps.");

    public void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/api/knowledge-bases/lookups/{lookupId:guid}", EditAsync)
            .WithTags("Knowledge bases")
            .WithName("EditLookup")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage);

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> EditAsync(
        Guid lookupId,
        LookupEditRequest request,
        KnowledgeBasesDbContext db,
        LookupCache cache,
        ILookupToolUsage lookupTools,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (name is not null && (name.Length == 0 || name.Length > Lookup.MaxNameLength || name.Any(char.IsControl)))
        {
            return InvalidName.ToProblem();
        }

        var description = request.Description?.Trim();
        if (description is not null && (description.Length == 0 || description.Length > Lookup.MaxDescriptionLength))
        {
            return InvalidDescription.ToProblem();
        }

        LookupStorage? storage = null;
        if (request.Storage is not null)
        {
            if (!DictionaryFieldRules.TryParseMember<LookupStorage>(request.Storage, out var parsed))
            {
                return StorageNotSupported.ToProblem();
            }

            storage = parsed;
        }

        // The tenant filter is on: a lookup another tenant shares reads as missing here, it is read-only through its own tenant.
        var lookup = await db.Lookups.SingleOrDefaultAsync(l => l.Id == lookupId, cancellationToken);
        if (lookup is null)
        {
            return TypedResults.NotFound();
        }

        // A manual lookup is its own source and an entity lookup is the only kind the store can serve (spec 012 D-4).
        if (storage is LookupStorage.Live && lookup.Kind != LookupKind.Entity)
        {
            return StorageNotSupported.ToProblem();
        }

        if (name is not null && name != lookup.Name && await db.Lookups.AnyAsync(l => l.Id != lookupId && l.Name == name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        List<LookupAttribute>? attributes = null;
        if (request.Attributes is not null)
        {
            var valid = LookupAttributeRules.Validate(request.Attributes, entity: lookup.Kind == LookupKind.Entity);
            if (!valid.IsSuccess)
            {
                return valid.Error.ToProblem();
            }

            attributes = valid.Value;

            // An attribute a tool fixes a condition on cannot go or change type under it (spec 012 D-3). The tools of the caller are named.
            var gone = lookup.Attributes.Where(old => !attributes.Any(n => n.Key == old.Key && n.Type == old.Type)).Select(old => old.Key).ToHashSet(StringComparer.Ordinal);
            var users = gone.Count == 0
                ? new List<LookupToolUse>()
                : (await lookupTools.ListAsync(cancellationToken))
                    .Where(t => t.AttributeKeys is { } keys && keys.TryGetValue(lookupId, out var names) && names.Any(gone.Contains))
                    .ToList();
            if (users.Count > 0)
            {
                var own = users.Where(t => t.TenantId == db.CurrentTenantId).Select(t => t.ToolName).Order(StringComparer.Ordinal).ToList();
                var message = own.Count > 0
                    ? $"The attribute is used by lookup tools {string.Join(", ", own)}: remove the condition from their configuration first."
                    : "The attribute is in use by lookup tools: remove the condition from their configuration first.";
                return Error.Conflict("lookup.attribute_in_use", message).ToProblem();
            }

            if (lookup.KnowledgeBaseId is { } knowledgeBaseId && attributes.Any(a => a.DictionaryFieldId is not null))
            {
                var mapped = await db.KnowledgeBases.Where(k => k.Id == knowledgeBaseId).Select(k => k.Mapping).SingleAsync(cancellationToken);
                if (attributes.Any(a => a.DictionaryFieldId is { } field && mapped.Fields.All(f => f.DictionaryFieldId != field)))
                {
                    return AttributeNotMapped.ToProblem();
                }
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        lookup.Rename(name ?? lookup.Name, description ?? lookup.Description);
        if (attributes is not null)
        {
            lookup.SetAttributes(attributes);
        }

        if (storage is { } mode && mode != lookup.Storage)
        {
            var slices = await db.LookupSlices.IgnoreQueryFilters().Where(s => s.LookupId == lookup.Id).ToListAsync(cancellationToken);
            lookup.Store(mode);
            foreach (var slice in slices)
            {
                if (mode == LookupStorage.Live)
                {
                    // No copy of the data stays once the store is the source: the generations go at once (spec 012 D-4).
                    slice.Empty();
                }
                else
                {
                    slice.RequestRefresh(clock.GetUtcNow());
                }
            }

            if (mode == LookupStorage.Live)
            {
                var sliceIds = slices.Select(s => s.Id).ToList();
                await db.LookupValues.IgnoreQueryFilters().Where(v => sliceIds.Contains(v.SliceId)).ExecuteDeleteAsync(cancellationToken);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        await cache.EvictLookupAsync(lookupId, cancellationToken);
        return TypedResults.NoContent();
    }
}
