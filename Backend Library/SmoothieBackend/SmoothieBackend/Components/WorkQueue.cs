using System.Collections.Concurrent;
using System.Collections.ObjectModel;

public sealed class WorkQueue<TKey> where TKey : notnull
{
    private readonly ConcurrentQueue<TKey> _queue = new();
    private readonly HashSet<TKey> _queued = new();
    private readonly HashSet<TKey> _processing = new();
    private readonly HashSet<TKey> _dirty = new();
    private readonly object _lock = new();

    private readonly bool _canBeDirty;
    
    public WorkQueue(bool canBeDirty = true)
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
                _queue.Enqueue(key);
        }
    }
    public bool TryDequeue(out TKey? outKey, CancellationToken ct = default)
    {
        if (!_queue.TryDequeue(out outKey))
            return false;
        
        lock (_lock)
        {
            _queued.Remove(outKey);
            _processing.Add(outKey);
        }
        return true;
    }
    
    public void Done(TKey key)
    {
        lock (_lock)
        {
            _processing.Remove(key);
            if (_canBeDirty || !_dirty.Remove(key))
                return;

            if (_queued.Add(key))
                _queue.Enqueue(key);
        }
    }
    
    public int Count {
        get {
            lock (_lock)
                return _queue.Count + _processing.Count;
        }
    }
}