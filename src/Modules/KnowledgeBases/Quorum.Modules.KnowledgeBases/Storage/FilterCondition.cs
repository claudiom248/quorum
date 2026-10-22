namespace Quorum.Modules.KnowledgeBases.Storage;

/// <summary>One condition of a search filter, on a path of the stored record. The conditions of a search are combined with AND (spec 003 D-11).</summary>
internal abstract record FilterCondition(string Path);

/// <summary>The value is one of <paramref name="Values"/>, or (for a list) the list contains one of them.</summary>
internal sealed record MatchAnyCondition(string Path, IReadOnlyList<string> Values) : FilterCondition(Path);

internal sealed record MatchBooleanCondition(string Path, bool Value) : FilterCondition(Path);

/// <summary>A number between two inclusive bounds, either of which may be open.</summary>
internal sealed record NumberRangeCondition(string Path, double? Min, double? Max) : FilterCondition(Path);

/// <summary>A date between two inclusive bounds (UTC), either of which may be open.</summary>
internal sealed record DateRangeCondition(string Path, DateTime? From, DateTime? To) : FilterCondition(Path);
