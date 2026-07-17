using System.Collections.Concurrent;

public sealed class BlockingWorkQueue<TKey> where TKey : notnull
{
    private readonly BlockingCollection<TKey> _queue = new();
    private readonly HashSet<TKey> _queued = new();
    private readonly HashSet<TKey> _processing = new();
    private readonly HashSet<TKey> _dirty = new();
    private readonly object _lock = new();

    private readonly bool _canBeDirty;
    
    public BlockingWorkQueue(bool canBeDirty = true)
    {
        _canBeDirty = canBeDirty;
    }
    
    public void Enqueue(TKey key)
    {
        lock (_lock)
        {
            if (_canBeDirty && _processing.Contains(key))
            {
                _dirty.Add(key);
                return;
            }

            if (_queued.Add(key))
            {
                _queue.Add(key);
            }
        }
    }
    public TKey Dequeue(CancellationToken ct = default)
    {
        var key = _queue.Take(ct);
        lock (_lock)
        {
            _queued.Remove(key);
            _processing.Add(key);
        }
        return key;
    }
    
    public void Done(TKey key)
    {
        lock (_lock)
        {
            _processing.Remove(key);
            if (_canBeDirty || !_dirty.Remove(key))
                return;

            if (_queued.Add(key))
                _queue.Add(key);
        }
    }
    
    public int Count => _queue.Count;
}