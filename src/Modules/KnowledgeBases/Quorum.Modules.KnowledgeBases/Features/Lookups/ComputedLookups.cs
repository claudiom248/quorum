using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>What saving a mapping did to the computed lookups of its knowledge base.</summary>
/// <param name="Due">A slice was created or made due: the refresh service has work.</param>
/// <param name="Removed">A lookup went with the field that stopped being listable: its cached lists are stale.</param>
internal readonly record struct LookupSync(bool Due, bool Removed);

/// <summary>
/// Keeps one computed lookup, with its slice of the whole knowledge base, for each listable field of a mapping (spec 007 D-3, D-10).
/// Nothing is computed here: a new or changed lookup is made due and the refresh service computes it in the background.
/// </summary>
internal static class ComputedLookups
{
    /// <param name="before">The mapping the knowledge base had, or <see langword="null"/> for a new one.</param>
    public static async Task<LookupSync> SyncAsync(
        KnowledgeBasesDbContext db,
        KnowledgeBase knowledgeBase,
        KnowledgeBaseMapping? before,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(knowledgeBase);

        var mapping = knowledgeBase.Mapping;
        var unit = LookupComputer.UnitOf(mapping);
        var wanted = mapping.Fields.Where(f => f.Listable).DistinctBy(f => f.DictionaryFieldId).ToList();
        var existing = before is null
            ? []
            : await db.Lookups.Where(l => l.KnowledgeBaseId == knowledgeBase.Id && l.Kind == LookupKind.Computed).ToListAsync(cancellationToken);

        var due = false;
        foreach (var field in wanted)
        {
            var lookup = existing.FirstOrDefault(l => l.DictionaryFieldId == field.DictionaryFieldId);
            if (lookup is null)
            {
                lookup = Lookup.Computed(knowledgeBase.TenantId, knowledgeBase.Id, field.DictionaryFieldId, unit, now);
                db.Lookups.Add(lookup);
                db.LookupSlices.Add(LookupSlice.Create(
                    knowledgeBase.TenantId, lookup.Id, LookupSliceKey.WholeKnowledgeBase.Hash, LookupSliceKey.WholeKnowledgeBase.Filters, now));
                due = true;
            }
            else if (ChangesValues(before!, mapping, field, lookup.Unit, unit))
            {
                lookup.CountAs(unit);
                foreach (var slice in await db.LookupSlices.IgnoreQueryFilters().Where(s => s.LookupId == lookup.Id).ToListAsync(cancellationToken))
                {
                    slice.RequestRefresh(now);
                }

                due = true;
            }
        }

        var gone = existing.Where(l => !wanted.Any(f => f.DictionaryFieldId == l.DictionaryFieldId)).ToList();
        db.Lookups.RemoveRange(gone);
        return new LookupSync(due, gone.Count > 0);
    }

    /// <summary>Whether the values or the counts of the lookup would differ under the new mapping.</summary>
    private static bool ChangesValues(KnowledgeBaseMapping before, KnowledgeBaseMapping after, MappedField field, LookupUnit? storedUnit, LookupUnit unit) =>
        storedUnit != unit
        || before.Fields.FirstOrDefault(f => f.DictionaryFieldId == field.DictionaryFieldId)?.Path != field.Path
        || before.CountedPoint != after.CountedPoint
        || before.Roles.DocumentId != after.Roles.DocumentId;
}
