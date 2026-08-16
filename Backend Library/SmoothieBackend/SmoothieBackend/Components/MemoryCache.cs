using System.Collections.Concurrent;
using SmoothieBackend.Models;

namespace SmoothieBackend.Components;

public class MemoryCache<TKey, TValue> where TKey : notnull
{
    private readonly MemoryCacheConfig _options;
    private readonly int _maxItemsClearTarget;

    private readonly DispatchTimer _cacheTickTimer;
    private readonly Lock _lock = new();
    
    private readonly ConcurrentDictionary<TKey, TValue> _cache = new();
    private readonly ConcurrentDictionary<TKey, ulong> _usageTally = new();

    public MemoryCache(MemoryCacheConfig options)
    {
        _options = options;
        
        _maxItemsClearTarget = (int)(_options.MaxItems * (1 - _options.CacheReductionPercentage));

        _cacheTickTimer = new DispatchTimer(_options.CacheTickSpan);
        _cacheTickTimer.OnTick += (_, _) => Tick();
    }

    private void Tick()
    {
        if (_cache.Count < _options.FreeItemsThreshold)
            return;

        lock (_lock)
        {
            int countRemoved;
            if (_cache.Count > _options.MaxItems)
                countRemoved = _cache.Count - _maxItemsClearTarget;
            else
                countRemoved = (int)(_cache.Count * _options.CacheReductionPercentage);
            
            if (countRemoved <= 0)
                return;
            
            var itemsToRemove = _usageTally.Select(x => (x.Value, x.Key)).OrderBy(x => x.Value).Take(countRemoved);
            foreach (var (_, key) in itemsToRemove)
            {
                _cache.TryRemove(key, out _);
                _usageTally.TryRemove(key, out _);
            }
        }
    }
    
    public bool TryAdd(TKey key, TValue value)
    {
        lock (_lock)
        {
            _usageTally.TryAdd(key, 0);
            return _cache.TryAdd(key, value);
        }
    }

    public bool TryGetValue(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_usageTally.TryGetValue(key, out var usage))
                _usageTally[key] = usage + 1;
            
            return _cache.TryGetValue(key, out value);
        }
    }
    
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _usageTally.Clear();
        }
    }
}