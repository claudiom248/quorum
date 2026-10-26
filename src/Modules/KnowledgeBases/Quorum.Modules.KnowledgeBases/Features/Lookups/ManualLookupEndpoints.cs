using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Registry;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// Manual lookups (spec 007 D-5): lists of values the administrator types, with a scope of their own. Their values live in the one
/// slice every lookup has, as a generation, so they are read like the lists computed from a knowledge base.
/// </summary>
internal sealed class ManualLookupEndpoints : IEndpointGroup
{
    private static readonly Error NameTaken = Error.Conflict("lookup.name_taken", "A lookup with this name already exists.");

    public void Map(IEndpointRouteBuilder app)
    {
        var lookups = app.MapGroup("/api/knowledge-bases/manual-lookups")
            .WithTags("Knowledge bases")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage);

        lookups.MapGet("/", ListAsync).WithName("ListManualLookups");
        lookups.MapPost("/", CreateAsync).WithName("CreateManualLookup");
        lookups.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateManualLookup");
        lookups.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteManualLookup");
    }

    private static async Task<Results<Ok<Page<ManualLookupDto>>, ProblemHttpResult>> ListAsync(HttpRequest request, KnowledgeBasesDbContext db, CancellationToken cancellationToken)
    {
        var query = ListQuery.Parse(request.Query);
        if (!query.IsSuccess)
        {
            return query.Error.ToProblem();
        }

        var filtered = ScopeFilter.Apply(LookupVisibility.Manual(db).AsNoTracking(), query.Value);
        if (!filtered.IsSuccess)
        {
            return filtered.Error.ToProblem();
        }

        // The value counts are read for the page only: one query for the page, one for the slices of its lookups.
        var page = await filtered.Value.ToPageAsync(query.Value, "manual-lookups", l => l.Name!, l => l.Id, l => l, cancellationToken);
        if (!page.IsSuccess)
        {
            return page.Error.ToProblem();
        }

        var ids = page.Value.Items.Select(l => l.Id).ToList();
        var counts = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking()
            .Where(s => ids.Contains(s.LookupId))
            .ToDictionaryAsync(s => s.LookupId, s => s.ValueCount, cancellationToken);
        var tenantId = db.CurrentTenantId;

        return TypedResults.Ok(new Page<ManualLookupDto>(
            [.. page.Value.Items.Select(l => ManualLookupDto.From(l, counts.GetValueOrDefault(l.Id), tenantId))],
            page.Value.Cursor));
    }

    private static async Task<Results<Created<CreatedLookupDto>, ProblemHttpResult>> CreateAsync(
        ManualLookupRequest request,
        KnowledgeBasesDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var valid = ManualLookupValidation.Validate(request);
        if (!valid.IsSuccess)
        {
            return valid.Error.ToProblem();
        }

        var manual = valid.Value;
        if (await db.Lookups.AnyAsync(l => l.Name == manual.Name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var now = clock.GetUtcNow();
        var tenantId = tenant.TenantId!.Value;
        var lookup = Lookup.Manual(tenantId, manual.Name, manual.Description, manual.Scope, manual.ScopeTenantIds, now);
        var slice = LookupSlice.Create(tenantId, lookup.Id, LookupSliceKey.WholeKnowledgeBase.Hash, LookupSliceKey.WholeKnowledgeBase.Filters, now);
        const int FirstGeneration = 1;
        slice.Publish(FirstGeneration, manual.Values.Count, now, nextRefreshAt: null);

        db.Lookups.Add(lookup);
        db.LookupSlices.Add(slice);
        db.LookupValues.AddRange(ToRows(tenantId, slice.Id, FirstGeneration, manual.Values));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            // A concurrent request took the name between the check and the insert.
            return NameTaken.ToProblem();
        }

        // No Location: a single manual lookup has no route to be read from; its values have.
        return TypedResults.Created((string?)null, new CreatedLookupDto(lookup.Id));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id,
        ManualLookupRequest request,
        KnowledgeBasesDbContext db,
        TimeProvider clock,
        LookupCache cache,
        CancellationToken cancellationToken)
    {
        var valid = ManualLookupValidation.Validate(request);
        if (!valid.IsSuccess)
        {
            return valid.Error.ToProblem();
        }

        var manual = valid.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, id, cancellationToken);

        // The tenant filter is on: a lookup another tenant shares is visible, never writable, and reads as missing.
        var lookup = await db.Lookups.SingleOrDefaultAsync(l => l.Id == id && l.Kind == LookupKind.Manual, cancellationToken);
        if (lookup is null)
        {
            return TypedResults.NotFound();
        }

        if (await db.Lookups.AnyAsync(l => l.Id != id && l.Name == manual.Name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        // The new values are a generation of their own and the old ones go in the same transaction, as with any computed list.
        var slice = await db.LookupSlices.SingleAsync(s => s.LookupId == id, cancellationToken);
        var previous = slice.CurrentGeneration;
        var generation = previous + 1;
        lookup.Revise(manual.Name, manual.Description, manual.Scope, manual.ScopeTenantIds);
        slice.Publish(generation, manual.Values.Count, clock.GetUtcNow(), nextRefreshAt: null);
        db.LookupValues.AddRange(ToRows(lookup.TenantId, slice.Id, generation, manual.Values));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        await db.LookupValues.Where(v => v.SliceId == slice.Id && v.Generation < generation).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await cache.EvictLookupAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(Guid id, KnowledgeBasesDbContext db, LookupCache cache, ILookupToolUsage lookupTools, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, id, cancellationToken);

        var lookup = await db.Lookups.SingleOrDefaultAsync(l => l.Id == id && l.Kind == LookupKind.Manual, cancellationToken);
        if (lookup is null)
        {
            return TypedResults.NotFound();
        }

        // Tools say which slices they use, and a manual lookup has the one. The tools of the caller are named, those of others counted.
        if (await db.LookupSlices.AnyAsync(s => s.LookupId == id && s.ToolIds.Count > 0, cancellationToken))
        {
            var users = (await lookupTools.ListAsync(cancellationToken)).Where(t => t.LookupIds.Contains(id)).ToList();
            var own = users.Where(t => t.TenantId == db.CurrentTenantId).Select(t => t.ToolName).Order(StringComparer.Ordinal).ToList();
            // Tools of other tenants are not named or counted: a tenant learns nothing about what it cannot see.
            var message = own.Count > 0
                ? $"The lookup is used by lookup tools {string.Join(", ", own)}: remove it from their configuration first."
                : "The lookup is in use: remove it from the lookup tools that use it first.";
            return Error.Conflict("lookup.in_use", message).ToProblem();
        }

        // The slice and its values go with the lookup, by the cascade of the foreign keys.
        db.Lookups.Remove(lookup);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await cache.EvictLookupAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Serialises the changes of one lookup: two updates would otherwise write the same generation.</summary>
    private static Task<int> LockAsync(KnowledgeBasesDbContext db, Guid lookupId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lookupId.ToString()}, 0))", cancellationToken);

    // The typed order is the order of the list: a manual lookup has no counts to rank by.
    private static IEnumerable<LookupValue> ToRows(Guid tenantId, Guid sliceId, int generation, List<ManualValue> values) =>
        values.Select((value, position) => LookupValue.Create(tenantId, sliceId, generation, position, value.Value, label: value.Label, synonyms: value.Synonyms));
}

internal sealed record CreatedLookupDto(Guid Id);

/// <param name="Shared"><see langword="true"/> for a lookup of another tenant: it can be read but not changed or deleted.</param>
/// <param name="Scope">The tenants a Tenant scope lists are the owner's to see: a lookup of another tenant shows its kind only.</param>
internal sealed record ManualLookupDto(Guid Id, string Name, string? Description, ScopeResponse Scope, int ValueCount, bool Shared)
{
    public static ManualLookupDto From(Lookup lookup, int valueCount, Guid? currentTenantId)
    {
        var shared = lookup.TenantId != currentTenantId;
        return new ManualLookupDto(lookup.Id, lookup.Name!, lookup.Description, new ScopeResponse(lookup.Scope!.Value.ToString(), shared ? [] : lookup.ScopeTenantIds, []), valueCount, shared);
    }
}
