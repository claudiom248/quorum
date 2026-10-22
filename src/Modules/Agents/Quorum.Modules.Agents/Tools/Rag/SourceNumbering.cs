namespace Quorum.Modules.Agents.Tools.Rag;

/// <summary>What makes two chunks the same source: where they come from, not their text.</summary>
internal readonly record struct SourceKey(string KnowledgeBase, string? DocumentId, string? Title, string? Position, string? Url);

/// <summary>
/// The numbers of the sources of one answer: they continue across the calls of every rag tool, so <c>[3]</c> means one source, and a source
/// that comes back in a later search keeps the number it had, so one source never has two.
/// </summary>
internal sealed class SourceNumbering
{
    private readonly Dictionary<SourceKey, int> _numbers = [];
    private int _last;

    /// <param name="isNew">Whether the source had no number yet, which is when it is published and stored.</param>
    public int NumberOf(SourceKey key, out bool isNew)
    {
        isNew = !_numbers.TryGetValue(key, out var number);
        if (isNew)
        {
            number = ++_last;
            _numbers[key] = number;
        }

        return number;
    }

    /// <summary>Takes back the numbers given to <paramref name="taken"/>: the result that used them was not returned to the model.</summary>
    public void Release(IReadOnlyCollection<SourceKey> taken)
    {
        foreach (var key in taken)
        {
            _numbers.Remove(key);
        }

        _last -= taken.Count;
    }
}
