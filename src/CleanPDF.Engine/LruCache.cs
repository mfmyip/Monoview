namespace CleanPDF.Engine;

/// <summary>Small least-recently-used cache. Not thread-safe.</summary>
public sealed class LruCache<TKey, TValue>(int capacity, Action<TValue>? onEvict = null) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();

    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            onEvict?.Invoke(existing.Value.Value);
        }
        var node = _order.AddFirst((key, value));
        _map[key] = node;
        while (_map.Count > capacity)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
            onEvict?.Invoke(last.Value.Value);
        }
    }

    public void Remove(TKey key)
    {
        if (!_map.Remove(key, out var node)) return;
        _order.Remove(node);
        onEvict?.Invoke(node.Value.Value);
    }

    public void RemoveWhere(Func<TKey, bool> match)
    {
        foreach (var key in _map.Keys.Where(match).ToList())
            Remove(key);
    }

    public void Clear()
    {
        foreach (var (_, value) in _order)
            onEvict?.Invoke(value);
        _order.Clear();
        _map.Clear();
    }
}
