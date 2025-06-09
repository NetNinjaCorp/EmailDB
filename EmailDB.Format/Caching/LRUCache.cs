using System;
using System.Collections.Generic;
using System.Threading;

namespace EmailDB.Format.Caching;

/// <summary>
/// Thread-safe LRU (Least Recently Used) cache implementation.
/// </summary>
public class LRUCache<TKey, TValue>
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cache;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly ReaderWriterLockSlim _lock;
    
    public LRUCache(int capacity)
    {
        _capacity = capacity;
        _cache = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
        _lruList = new LinkedList<CacheItem>();
        _lock = new ReaderWriterLockSlim();
    }
    
    public bool TryGet(TKey key, out TValue value)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lock.EnterWriteLock();
                try
                {
                    // Move to front (most recently used)
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
                
                value = node.Value.Value;
                return true;
            }
            
            value = default;
            return false;
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }
    
    public void Set(TKey key, TValue value)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                // Update existing
                node.Value.Value = value;
                _lruList.Remove(node);
                _lruList.AddFirst(node);
            }
            else
            {
                // Add new
                if (_cache.Count >= _capacity)
                {
                    // Remove least recently used
                    var lru = _lruList.Last;
                    _cache.Remove(lru.Value.Key);
                    _lruList.RemoveLast();
                }
                
                var cacheItem = new CacheItem { Key = key, Value = value };
                var newNode = _lruList.AddFirst(cacheItem);
                _cache[key] = newNode;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _cache.Clear();
            _lruList.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _cache.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }
    
    private class CacheItem
    {
        public TKey Key { get; set; }
        public TValue Value { get; set; }
    }
    
    public void Dispose()
    {
        _lock?.Dispose();
    }
}