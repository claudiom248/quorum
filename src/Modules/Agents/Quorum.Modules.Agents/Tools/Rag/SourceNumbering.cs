namespace Quorum.Modules.Agents.Tools.Rag;

/// <summary>The numbers of the sources of one answer: they continue across the calls of every rag tool, so <c>[3]</c> means one source.</summary>
internal sealed class SourceNumbering
{
    private int _last;

    public int Next() => ++_last;

    /// <summary>Takes back the last <paramref name="count"/> numbers: the result that used them was not returned to the model.</summary>
    public void Release(int count) => _last -= count;
}
