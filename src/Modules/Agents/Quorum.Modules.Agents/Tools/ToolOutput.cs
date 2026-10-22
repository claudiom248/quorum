namespace Quorum.Modules.Agents.Tools;

/// <summary>A source a tool cites in its result: references only, never the text (spec 003 D-15).</summary>
internal sealed record ToolSource(int N, string KnowledgeBase, string? DocumentId, string? Title, string? Position, string? Url);

/// <summary>
/// What a tool call returns: the text the model reads and, for tools that cite, the sources it numbered for the first time (a source
/// cited again keeps its earlier number and is not listed again). <paramref name="Discard"/> undoes what the numbering took when the
/// result is not returned. <paramref name="SourceCount"/> is how many sources the result cites, new or not.
/// </summary>
internal sealed record ToolOutput(string Text, IReadOnlyList<ToolSource>? Sources = null, Action? Discard = null, int? SourceCount = null);
