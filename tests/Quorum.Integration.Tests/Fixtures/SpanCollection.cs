using System.Collections;
using System.Diagnostics;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// The spans an app exported to its in-memory exporter. Spans arrive on the thread that ends them, so reads take the lock.
/// </summary>
public sealed class SpanCollection : ICollection<Activity>
{
    private readonly List<Activity> _spans = [];

    public int Count
    {
        get { lock (_spans) { return _spans.Count; } }
    }

    public bool IsReadOnly => false;

    public void Add(Activity item)
    {
        lock (_spans)
        {
            _spans.Add(item);
        }
    }

    /// <summary>A copy of the spans so far, in the order they ended.</summary>
    public IReadOnlyList<Activity> Snapshot()
    {
        lock (_spans)
        {
            return _spans.ToArray();
        }
    }

    public void Clear()
    {
        lock (_spans)
        {
            _spans.Clear();
        }
    }

    public bool Contains(Activity item)
    {
        lock (_spans)
        {
            return _spans.Contains(item);
        }
    }

    public void CopyTo(Activity[] array, int arrayIndex)
    {
        lock (_spans)
        {
            _spans.CopyTo(array, arrayIndex);
        }
    }

    public bool Remove(Activity item)
    {
        lock (_spans)
        {
            return _spans.Remove(item);
        }
    }

    public IEnumerator<Activity> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
