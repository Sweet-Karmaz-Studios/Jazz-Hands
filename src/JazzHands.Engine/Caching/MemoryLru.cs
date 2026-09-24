namespace JazzHands.Engine.Caching;

/// <summary>
/// A thread-safe least recently used map with a budget, each entry weighing what its caller says.
/// </summary>
/// <remarks>
/// For thumbnails (weighed as one each, 4000 of them) and waveforms (weighed in bytes). Reads come
/// from the UI thread while workers write, so everything is under one lock; the work inside it is
/// a dictionary lookup and a linked list splice.
/// </remarks>
/// <typeparam name="TKey">The key.</typeparam>
/// <typeparam name="TValue">The value.</typeparam>
public sealed class MemoryLru<TKey, TValue>
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Weight)>> _entries;
    private readonly LinkedList<(TKey Key, TValue Value, long Weight)> _order = new();
    private readonly Func<TValue, long> _weigh;
    private long _weight;

    /// <summary>A map that holds at most <paramref name="budget"/> of weight.</summary>
    /// <param name="budget">What the entries may weigh together.</param>
    /// <param name="weigh">An entry's weight; one each when null.</param>
    /// <param name="comparer">How keys compare.</param>
    public MemoryLru(long budget, Func<TValue, long>? weigh = null, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        Budget = budget;
        _weigh = weigh ?? (_ => 1);
        _entries = new Dictionary<TKey, LinkedListNode<(TKey, TValue, long)>>(comparer);
    }

    /// <summary>What the entries may weigh together.</summary>
    public long Budget { get; }

    /// <summary>What they weigh now.</summary>
    public long Weight
    {
        get
        {
            lock (_gate)
            {
                return _weight;
            }
        }
    }

    /// <summary>How many entries there are.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>An entry, marking it used.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<(TKey, TValue Value, long)>? node))
            {
                _order.Remove(node);
                _order.AddLast(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>Adds or replaces an entry, evicting the least recently used past the budget. The new one stays.</summary>
    public void Set(TKey key, TValue value)
    {
        long weight = _weigh(value);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<(TKey, TValue, long Weight)>? old))
            {
                _weight -= old.Value.Weight;
                _order.Remove(old);
            }

            _entries[key] = _order.AddLast((key, value, weight));
            _weight += weight;

            while (_weight > Budget && _order.First is { } oldest && oldest != _order.Last)
            {
                _order.RemoveFirst();
                _entries.Remove(oldest.Value.Key);
                _weight -= oldest.Value.Weight;
            }
        }
    }

    /// <summary>Removes every entry whose key matches.</summary>
    public int RemoveWhere(Func<TKey, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        lock (_gate)
        {
            var doomed = _entries.Keys.Where(match).ToList();
            foreach (TKey key in doomed)
            {
                LinkedListNode<(TKey, TValue, long Weight)> node = _entries[key];
                _order.Remove(node);
                _entries.Remove(key);
                _weight -= node.Value.Weight;
            }

            return doomed.Count;
        }
    }

    /// <summary>Removes everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
            _weight = 0;
        }
    }
}
