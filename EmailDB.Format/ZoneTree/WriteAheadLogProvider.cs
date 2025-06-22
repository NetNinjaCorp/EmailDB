using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Serializers;
using Tenray.ZoneTree.WAL;
using Tenray.ZoneTree.Exceptions.WAL;
using System.Collections.Concurrent;

namespace EmailDB.Format.ZoneTree;

public class WriteAheadLogProvider : IWriteAheadLogProvider
{
    private readonly RawBlockManager _blockManager;
    private readonly string _name;
    private readonly Dictionary<string, object> _logs;

    public WriteAheadLogProvider(RawBlockManager blockManager, string name)
    {
        _blockManager = blockManager;
        _name = name;
        _logs = new Dictionary<string, object>();
    }

    public void InitCategory(string category)
    {
        // Initialize category if needed
    }

    public IWriteAheadLog<TK, TV> GetOrCreateWAL<TK, TV>(
        long segmentId,
        string category,
        WriteAheadLogOptions options,
        ISerializer<TK> keySerializer,
        ISerializer<TV> valueSerializer)
    {
        var key = GetWALKey(segmentId, category);

        if (_logs.TryGetValue(key, out var existing))
        {
            if (existing is IWriteAheadLog<TK, TV> typedExisting)
            {
                return typedExisting;
            }
            throw new InvalidOperationException($"Existing WAL for key '{key}' has incompatible types.");
        }

        // Create a simple in-memory WAL for metadata persistence
        var wal = new InMemoryWriteAheadLog<TK, TV>(segmentId, category);
        _logs[key] = wal;
        return wal;
    }

    public IWriteAheadLog<TKey, TValue> GetWAL<TKey, TValue>(long segmentId, string category)
    {
        var key = GetWALKey(segmentId, category);
        if (_logs.TryGetValue(key, out var wal))
        {
            return (IWriteAheadLog<TKey, TValue>)wal;
        }
        return null;
    }

    public bool RemoveWAL(long segmentId, string category)
    {
        var key = GetWALKey(segmentId, category);
        if (_logs.TryGetValue(key, out var wal))
        {
            return _logs.Remove(key);
        }
        return false;
    }

    public void DropStore()
    {
        _logs.Clear();
    }

    private string GetWALKey(long segmentId, string category)
    {
        return $"{segmentId}_{category}";
    }
}

/// <summary>
/// Simple in-memory WAL implementation that doesn't persist anything.
/// This is used to satisfy ZoneTree's requirements while actual persistence
/// is handled by the RandomAccessDevice and block storage.
/// </summary>
public class InMemoryWriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue>
{
    private readonly long _segmentId;
    private readonly string _category;
    private readonly List<LogEntry> _entries = new();
    private bool _isDisposed;

    public string FilePath => $"memory://wal_{_segmentId}_{_category}";
    public int InitialLength => 0;
    public bool EnableIncrementalBackup { get; set; }

    public InMemoryWriteAheadLog(long segmentId, string category)
    {
        _segmentId = segmentId;
        _category = category;
    }

    public void Append(in TKey key, in TValue value, long opIndex)
    {
        // In-memory only, no persistence needed
        _entries.Add(new LogEntry { OpIndex = opIndex });
    }

    public void Drop()
    {
        _entries.Clear();
    }

    public WriteAheadLogReadLogEntriesResult<TKey, TValue> ReadLogEntries(
        bool stopReadOnException,
        bool stopReadOnChecksumFailure,
        bool sortByOpIndexes)
    {
        // Return empty result as we don't persist WAL entries
        return new WriteAheadLogReadLogEntriesResult<TKey, TValue>
        {
            Success = true,
            Keys = Array.Empty<TKey>(),
            Values = Array.Empty<TValue>(),
            MaximumOpIndex = 0
        };
    }

    public long ReplaceWriteAheadLog(TKey[] keys, TValue[] values, bool disableBackup)
    {
        // Clear current entries and return 0 as we don't persist
        _entries.Clear();
        return 0;
    }

    public void TruncateIncompleteTailRecord(IncompleteTailRecordFoundException incompleteTailException)
    {
        // No-op for in-memory WAL
    }

    public void MarkFrozen()
    {
        // No-op for in-memory WAL
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _entries.Clear();
            _isDisposed = true;
        }
    }

    private class LogEntry
    {
        public long OpIndex { get; set; }
    }
}

// Simple WAL implementation that stores entries in EmailDB blocks
// Temporarily commented out due to interface compatibility issues
/*public class WriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue>, IWriteAheadLogBase
{
    private readonly RawBlockManager _blockManager;
    private readonly string _name;
    private readonly long _segmentId;
    private readonly string _category;
    private readonly int _blockId;
    private readonly List<WALEntry<TKey, TValue>> _entries;
    private bool _isDisposed;

    public string FilePath { get; }
    public bool EnableIncrementalBackup { get; set; }
    public int InitialLength { get; private set; }

    public WriteAheadLog(RawBlockManager blockManager, string name, long segmentId, string category)
    {
        _blockManager = blockManager;
        _name = name;
        _segmentId = segmentId;
        _category = category;
        FilePath = $"{name}_wal_{segmentId}_{category}";
        _blockId = FilePath.GetHashCode();
        _entries = new List<WALEntry<TKey, TValue>>();
        
        LoadExistingEntries();
    }

    private void LoadExistingEntries()
    {
        // For simplicity, we'll start with empty WAL each time
        // In a full implementation, we'd deserialize existing WAL entries
        InitialLength = 0;
    }

    public void Append(in TKey key, in TValue value, long opIndex)
    {
        _entries.Add(new WALEntry<TKey, TValue> { Key = key, Value = value, OpIndex = opIndex });
    }

    public void Drop()
    {
        _entries.Clear();
        _isDisposed = true;
    }

    public void MarkFrozen()
    {
        // Save current entries to EmailDB block
        SaveEntriesToBlock();
    }

    private void SaveEntriesToBlock()
    {
        // For simplicity, we'll serialize entries as JSON
        // In a production implementation, you'd use a more efficient serialization
        var serializedEntries = System.Text.Json.JsonSerializer.Serialize(_entries);
        var entryBytes = System.Text.Encoding.UTF8.GetBytes(serializedEntries);

        var block = new Block
        {
            Version = 1,
            Type = BlockType.WAL,
            Flags = 0,
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = _blockId,
            Payload = entryBytes
        };

        _blockManager.WriteBlockAsync(block).Wait();
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            SaveEntriesToBlock();
            _isDisposed = true;
        }
    }
}

public class WALEntry<TKey, TValue>
{
    public TKey Key { get; set; }
    public TValue Value { get; set; }
    public long OpIndex { get; set; }
}

public interface IWriteAheadLogBase : IDisposable
{
    string FilePath { get; }
    bool EnableIncrementalBackup { get; set; }
    int InitialLength { get; }
    void Drop();
    void MarkFrozen();
}*/