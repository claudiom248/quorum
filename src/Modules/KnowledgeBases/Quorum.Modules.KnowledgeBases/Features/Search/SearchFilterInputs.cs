using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Dictionary;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <summary>
/// Turns the filters a tool call carries (the model's and the tool's fixed ones) into checked filters on dictionary fields. The search
/// and the live operations of the lookups read them the same way, so a filter means the same thing in both.
/// </summary>
internal static class SearchFilterInputs
{
    /// <param name="filterFields">The parameters the caller named (a tool's aliases) with their field; without them the parameters are the names of the fields themselves.</param>
    /// <param name="fixedFilterFieldIds">The fields the fixed filters name; a fixed filter that is not an alias is read among these.</param>
    /// <param name="bases">The knowledge bases of the call, whose mapped fields name the fixed filters when <paramref name="fixedFilterFieldIds"/> is not given.</param>
    public static async Task<Result<IReadOnlyList<FieldFilter>>> ParseAsync(
        KnowledgeBasesDbContext db,
        IReadOnlyDictionary<string, JsonElement> filters,
        IReadOnlyDictionary<string, JsonElement> fixedFilters,
        IReadOnlyDictionary<string, Guid>? filterFields,
        IReadOnlyList<Guid>? fixedFilterFieldIds,
        IReadOnlyList<KnowledgeBase> bases,
        CancellationToken cancellationToken)
    {
        var visible = await DictionaryVisibility.Fields(db).AsNoTracking().ToListAsync(cancellationToken);

        // The model's filters use the parameters the caller named (a rag tool's aliases); without them, the names of the fields themselves.
        IReadOnlyDictionary<string, FilterField> fields;
        IReadOnlyDictionary<string, FilterField> fixedFields;
        FixedFilterNames? names = null;
        if (filterFields is { } named)
        {
            fields = named
                .Select(pair => (pair.Key, Field: visible.FirstOrDefault(f => f.Id == pair.Value)))
                .Where(entry => entry.Field is not null)
                .ToDictionary(entry => entry.Key, entry => new FilterField(entry.Field!.Id, entry.Key, entry.Field.Type), StringComparer.Ordinal);

            // A fixed filter may also name the other fields it was saved with, by their own name (spec 003 A1-4): the tool's, not the ones of
            // the knowledge bases this search was narrowed to, or narrowing would turn a filter into an error instead of skipping a knowledge base.
            var mapped = fixedFilterFieldIds?.ToHashSet() ?? bases.SelectMany(k => k.Mapping.Fields.Select(f => f.DictionaryFieldId)).ToHashSet();
            names = FixedFilterNames.Build(
                named.Where(pair => visible.Any(f => f.Id == pair.Value)).ToDictionary(pair => pair.Key, pair => visible.First(f => f.Id == pair.Value), StringComparer.Ordinal),
                visible.Where(f => mapped.Contains(f.Id)));
            fixedFields = names.Fields;
        }
        else
        {
            fields = visible
                .OrderBy(f => f.Cluster ?? string.Empty, StringComparer.Ordinal)
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new FilterField(g.First().Id, g.Key, g.First().Type), StringComparer.Ordinal);
            fixedFields = fields;
        }

        var parsedFixed = names?.AmbiguityProblem(fixedFilters.Keys) is not null
            ? Result<IReadOnlyList<FieldFilter>>.Failure(Error.Validation(KnowledgeBaseSearchErrors.InvalidFixedFilter, "ambiguous"))
            : SearchFilterParser.Parse(fixedFilters, fixedFields, KnowledgeBaseSearchErrors.InvalidFixedFilter, ignoreEmpty: false);
        if (!parsedFixed.IsSuccess)
        {
            // The fixed filters are never shown to the model, so what is wrong with them is not said either.
            return Error.Validation(
                KnowledgeBaseSearchErrors.InvalidFixedFilter,
                "A filter that the tool always applies is not valid for the knowledge bases. An administrator has to correct the tool.");
        }

        var modelFilters = SearchFilterParser.Parse(filters, fields, KnowledgeBaseSearchErrors.InvalidFilter, ignoreEmpty: true);
        if (!modelFilters.IsSuccess)
        {
            return modelFilters.Error;
        }

        // Both always apply: a value the model gives for a field the binding fixed can only narrow the fixed one, never replace it.
        return Result<IReadOnlyList<FieldFilter>>.Success([.. parsedFixed.Value, .. modelFilters.Value]);
    }
}
