using EmailDB.Format.Models;
using ProtoBuf;
using System.Collections.Concurrent;
using Tenray.ZoneTree.Exceptions.WAL;
using Tenray.ZoneTree.WAL;

namespace EmailDB.Format.ZoneTree;

/// <summary>
/// WriteAheadLog implementation using EmailDB storage
/// </summary>
public class WriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue> , IWriteAheadLogBase
{
    private readonly StorageManager storageManager;
    private readonly string folderName;
    private bool isFrozen;
    private readonly object writeLock = new object();
    private readonly ConcurrentDictionary<long, WALEntry<TKey, TValue>> entryCache;

    public WriteAheadLog(StorageManager storageManager, string name, long segmentId, string category)
    {
        this.storageManager = storageManager;
        this.folderName = $"{name}_wal_{category}_{segmentId}";
        this.entryCache = new ConcurrentDictionary<long, WALEntry<TKey, TValue>>();

        // Ensure WAL folder exists
        storageManager.CreateFolder(folderName);
    }

    public string FilePath => folderName;
    public bool EnableIncrementalBackup { get; set; }
    public int InitialLength { get; private set; }

    public void Append(in TKey key, in TValue value, long opIndex)
    {
        if (isFrozen) return;

        lock (writeLock)
        {
            var entry = new WALEntry<TKey, TValue>
            {
                Key = key,
                Value = value,
                OpIndex = opIndex
            };

            using var ms = new MemoryStream();
            Serializer.SerializeWithLengthPrefix(ms, entry, PrefixStyle.Base128);
            storageManager.AddEmailToFolder(folderName, ms.ToArray());
            entryCache[opIndex] = entry;
        }
    }

    public void Drop()
    {
        lock (writeLock)
        {
            storageManager.DeleteFolder(folderName, true);
            entryCache.Clear();
        }
    }

    public WriteAheadLogReadLogEntriesResult<TKey, TValue> ReadLogEntries(
        bool stopReadOnException,
        bool stopReadOnChecksumFailure,
        bool sortByOpIndexes)
    {
        var entries = new List<WALEntry<TKey, TValue>>();
        var result = new WriteAheadLogReadLogEntriesResult<TKey, TValue>();

        try
        {
            // Read entries from storage
            foreach (var (_, block) in storageManager.WalkBlocks())
            {
                if (block.Content is SegmentContent segment)
                {
                    try
                    {
                        using var ms = new MemoryStream(segment.SegmentData);
                        var entry = Serializer.DeserializeWithLengthPrefix<WALEntry<TKey, TValue>>(ms, PrefixStyle.Base128);
                        if (entry != null)
                        {
                            entries.Add(entry);
                            entryCache[entry.OpIndex] = entry;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (stopReadOnException)
                        {
                            result.Exceptions[entries.Count] = ex;
                            break;
                        }
                    }
                }
            }

            if (sortByOpIndexes)
            {
                entries.Sort((a, b) => a.OpIndex.CompareTo(b.OpIndex));
            }

            result.Success = true;
            result.Keys = entries.Select(e => e.Key).ToList();
            result.Values = entries.Select(e => e.Value).ToList();
            result.MaximumOpIndex = entries.Count > 0 ? entries.Max(e => e.OpIndex) : 0;
            InitialLength = entries.Count;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Exceptions[0] = ex;
        }

        return result;
    }

    public long ReplaceWriteAheadLog(TKey[] keys, TValue[] values, bool disableBackup)
    {
        lock (writeLock)
        {
            if (!disableBackup && EnableIncrementalBackup)
            {
                BackupCurrentLog();
            }

            // Clear existing entries
            storageManager.DeleteFolder(folderName, true);
            storageManager.CreateFolder(folderName);
            entryCache.Clear();

            // Write new entries
            for (int i = 0; i < keys.Length; i++)
            {
                var entry = new WALEntry<TKey, TValue>
                {
                    Key = keys[i],
                    Value = values[i],
                    OpIndex = i
                };

                using var ms = new MemoryStream();
                Serializer.SerializeWithLengthPrefix(ms, entry, PrefixStyle.Base128);
                storageManager.AddEmailToFolder(folderName, ms.ToArray());
                entryCache[i] = entry;
            }

            return 0;
        }
    }

    private void BackupCurrentLog()
    {
        var backupFolder = $"{folderName}_backup";
        storageManager.CreateFolder(backupFolder);

        foreach (var entry in entryCache.Values)
        {
            using var ms = new MemoryStream();
            Serializer.SerializeWithLengthPrefix(ms, entry, PrefixStyle.Base128);
            storageManager.AddEmailToFolder(backupFolder, ms.ToArray());
        }
    }

    public void MarkFrozen()
    {
        isFrozen = true;
    }

    public void TruncateIncompleteTailRecord(IncompleteTailRecordFoundException incompleteTailException)
    {
        // No-op as we use atomic operations
    }

    public void Dispose()
    {
        entryCache.Clear();
    }
}
