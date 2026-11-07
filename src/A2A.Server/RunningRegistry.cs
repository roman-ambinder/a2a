namespace A2A.Server;

/// <summary>
/// The executions running per task (keyed by the in-memory scope key). Process-local. Add and remove are atomic with
/// respect to each other, so an execution that was added is visible until it is removed: emptying a task's set never
/// detaches a set another add just used.
/// </summary>
internal sealed class RunningRegistry<T> where T : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<T>> _map = new(StringComparer.Ordinal);

    public void Add(string scope, T item)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(scope, out var set)) _map[scope] = set = new HashSet<T>(ReferenceEqualityComparer.Instance);
            set.Add(item);
        }
    }

    public void Remove(string scope, T item)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(scope, out var set) && set.Remove(item) && set.Count == 0) _map.Remove(scope);
        }
    }

    /// <summary>The items running for <paramref name="scope"/> now.</summary>
    public List<T> Snapshot(string scope)
    {
        lock (_gate) return _map.TryGetValue(scope, out var set) ? [.. set] : [];
    }

    /// <summary>Every running item.</summary>
    public List<T> All()
    {
        lock (_gate) return _map.Values.SelectMany(s => s).ToList();
    }
}
