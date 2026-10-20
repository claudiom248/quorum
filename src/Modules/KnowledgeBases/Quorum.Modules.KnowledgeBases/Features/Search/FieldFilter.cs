using Quorum.Modules.KnowledgeBases.Domain;

namespace Quorum.Modules.KnowledgeBases.Features.Search;

/// <summary>A dictionary field a search can filter on.</summary>
internal sealed record FilterField(Guid Id, string Name, DictionaryFieldType Type);

/// <summary>A validated filter on a dictionary field, independent of any knowledge base: it still has to be mapped onto each one's payload.</summary>
internal abstract record FieldFilter(FilterField Field);

/// <summary>A text equals one of the values, or a list of texts contains one of them.</summary>
internal sealed record AnyOf(FilterField Field, IReadOnlyList<string> Values) : FieldFilter(Field);

internal sealed record IsBoolean(FilterField Field, bool Value) : FieldFilter(Field);

internal sealed record NumberRange(FilterField Field, double? Min, double? Max) : FieldFilter(Field);

/// <summary>Inclusive instants. A date given without a time as the end already stands for the last instant of that day.</summary>
internal sealed record DateRange(FilterField Field, DateTimeOffset? From, DateTimeOffset? To) : FieldFilter(Field);
