namespace Quorum.Modules.KnowledgeBases.Storage.Qdrant;

/// <summary>One condition of a search filter, on a payload path. The conditions of a search are combined with AND (spec 003 D-11).</summary>
internal abstract record QdrantCondition(string Path);

/// <summary>The value is one of <paramref name="Values"/>, or (for a list payload) the list contains one of them.</summary>
internal sealed record MatchAnyCondition(string Path, IReadOnlyList<string> Values) : QdrantCondition(Path);

internal sealed record MatchBooleanCondition(string Path, bool Value) : QdrantCondition(Path);

/// <summary>A number between two inclusive bounds, either of which may be open.</summary>
internal sealed record NumberRangeCondition(string Path, double? Min, double? Max) : QdrantCondition(Path);

/// <summary>A date between two inclusive bounds (UTC), either of which may be open. Payload dates are ISO 8601 strings.</summary>
internal sealed record DateRangeCondition(string Path, DateTime? From, DateTime? To) : QdrantCondition(Path);
