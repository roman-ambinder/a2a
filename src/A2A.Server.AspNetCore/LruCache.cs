namespace A2A.Server.AspNetCore;

/// <summary>
///     A thread-safe cache of at most <c>capacity</c> entries. When it is full, adding an entry evicts the least recently
///     used one
///     (a read or a write counts as a use), so entries in steady use stay while one-off entries pass through.
/// </summary>
internal sealed class LruCache<TKey, TValue>(int capacity, IEqualityComparer<TKey>? comparer = null)
    where TKey : notnull
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new(comparer);
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new(); // most recently used first

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>Adds or replaces the entry and marks it most recently used; evicts the least recently used one when full.</summary>
    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            else if (_map.Count >= _capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }

            _map[key] = _order.AddFirst((key, value));
        }
    }
}