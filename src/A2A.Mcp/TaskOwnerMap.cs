namespace A2A.Mcp;

/// <summary>
/// A bounded map from task id and context id to the owner the bridge issued it to. Task ids and context ids are separate key
/// spaces. The oldest entries are dropped first. The first owner of an id keeps it.
/// </summary>
internal sealed class TaskOwnerMap(int capacity)
{
    private readonly Dictionary<(bool IsContext, string Id), string> _owners = [];
    private readonly Queue<(bool IsContext, string Id)> _order = new();
    private readonly object _gate = new();

    internal void RememberTask(string? id, string owner) => Remember((false, id), owner);

    internal void RememberContext(string? id, string owner) => Remember((true, id), owner);

    internal bool OwnsTask(string id, string owner) => Owns((false, id), owner);

    internal bool OwnsContext(string id, string owner) => Owns((true, id), owner);

    private void Remember((bool IsContext, string? Id) key, string owner)
    {
        if (string.IsNullOrEmpty(key.Id)) return;
        var entry = (key.IsContext, key.Id);
        lock (_gate)
        {
            if (!_owners.TryAdd(entry, owner)) return;
            _order.Enqueue(entry);
            while (_order.Count > capacity) _owners.Remove(_order.Dequeue());
        }
    }

    private bool Owns((bool IsContext, string Id) key, string owner)
    {
        lock (_gate) return _owners.TryGetValue(key, out var existing) && string.Equals(existing, owner, StringComparison.Ordinal);
    }
}
