//using EmailDB.Format.Models.BlockTypes;
//using global::EmailDB.Format.Models;
//using ProtoBuf;
//using Tenray.ZoneTree.AbstractFileStream;
//using Tenray.ZoneTree.Options;
//using Tenray.ZoneTree.Serializers;
//using Tenray.ZoneTree.WAL;

//namespace EmailDB.Format.ZoneTree;

//public class WriteAheadLogProvider : IWriteAheadLogProvider
//{
//    private readonly StorageManager storageManager;
//    private readonly string name;
//    private readonly Dictionary<string, IWriteAheadLogBase> logs;
//    private long walBlockOffset = -1;

//    public WriteAheadLogProvider(StorageManager storageManager, string name)
//    {
//        this.storageManager = storageManager;
//        this.name = name;
//        this.logs = new Dictionary<string, IWriteAheadLogBase>();

//        // Initialize WAL block if needed
//        InitializeWAL();
//    }

//    private void InitializeWAL()
//    {
//        var metadata = GetMetadataContent();
//        if (metadata != null)
//        {
//            walBlockOffset = metadata.WALOffset;
//            if (walBlockOffset == -1)
//            {
//                // Create initial WAL block
//                var walContent = new WALContent();
//                var walBlock = new Block
//                {
//                    Header = new BlockHeader
//                    {
//                        Type = BlockType.WAL,
//                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
//                        Version = 1
//                    },
//                    Content = walContent
//                };

//                walBlockOffset = storageManager.WriteBlock(walBlock);
//                metadata.WALOffset = walBlockOffset;
//                var tmpMD = storageManager.segmentManager.GetMetadata();
//                tmpMD.WALOffset = walBlockOffset;
//                storageManager.segmentManager.UpdateMetadata(tmpMD);
//            }
//        }
//    }

//    public void InitCategory(string category)
//    {
//        var walContent = GetWALContent();
//        if (!walContent.CategoryOffsets.ContainsKey(category))
//        {
//            walContent.CategoryOffsets[category] = -1;
//            UpdateWALContent(walContent);
//        }
//    }

//    public IWriteAheadLog<TK, TV> GetOrCreateWAL<TK, TV>(
//     long segmentId,
//     string category,
//     WriteAheadLogOptions options,
//     ISerializer<TK> keySerializer,
//     ISerializer<TV> valueSerializer)
//    {
//        var key = GetWALKey(segmentId, category);

//        if (logs.TryGetValue(key, out var existing))
//        {
//            if (existing is IWriteAheadLog<TK, TV> typedExisting)
//            {
//                return typedExisting;
//            }
//            throw new InvalidOperationException($"Existing WAL for key '{key}' has incompatible types.");
//        }

//        var wal = new WriteAheadLog<TK, TV>(
//            storageManager,
//            name,
//            segmentId,
//            category);

//        logs[key] = wal; // Store the generic WAL
//        return wal;
//    }

//    public IWriteAheadLog<TKey, TValue> GetWAL<TKey, TValue>(long segmentId, string category)
//    {
//        var key = GetWALKey(segmentId, category);
//        if (logs.TryGetValue(key, out var wal))
//        {
//            return (IWriteAheadLog<TKey, TValue>)wal;
//        }
//        return null;
//    }

//    public bool RemoveWAL(long segmentId, string category)
//    {
//        var key = GetWALKey(segmentId, category);
//        if (logs.TryGetValue(key, out var wal))
//        {
//            wal.Drop();
//            return logs.Remove(key);
//        }
//        return false;
//    }

//    public void DropStore()
//    {
//        foreach (var wal in logs.Values)
//        {
//            wal.Drop();
//        }
//        logs.Clear();
//    }

//    internal WALContent GetWALContent()
//    {
//        if (walBlockOffset != -1)
//        {
//            var block = storageManager.ReadBlock(walBlockOffset);
//            if (block?.Content is WALContent walContent)
//            {
//                return walContent;
//            }
//        }
//        return new WALContent();
//    }

//    internal void UpdateWALContent(WALContent content)
//    {
//        var block = new Block
//        {
//            Header = new BlockHeader
//            {
//                Type = BlockType.WAL,
//                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
//                Version = 1
//            },
//            Content = content
//        };

//        storageManager.WriteBlock(block, walBlockOffset);
//    }

//    private string GetWALKey(long segmentId, string category)
//    {
//        return $"{segmentId}_{category}";
//    }

//    private MetadataContent GetMetadataContent()
//    {
//        var header = storageManager.GetHeader();
//        if (header.FirstMetadataOffset != -1)
//        {
//            var block = storageManager.ReadBlock(header.FirstMetadataOffset);
//            return block?.Content as MetadataContent;
//        }
//        return null;
//    }
//}

//[ProtoContract]
//public class WALEntry<TKey, TValue>
//{
//    [ProtoMember(1)]
//    public TKey Key { get; set; }

//    [ProtoMember(2)]
//    public TValue Value { get; set; }

//    [ProtoMember(3)]
//    public long OpIndex { get; set; }
//}

//public class WriteAheadLogWrapper
//{
//    public object WriteAheadLog { get; }
//    public Type KeyType { get; }
//    public Type ValueType { get; }

//    public WriteAheadLogWrapper(object writeAheadLog, Type keyType, Type valueType)
//    {
//        WriteAheadLog = writeAheadLog;
//        KeyType = keyType;
//        ValueType = valueType;
//    }
//}

//public interface IWriteAheadLogBase : IDisposable
//{
//    string FilePath { get; }
//    bool EnableIncrementalBackup { get; set; }
//    int InitialLength { get; }
//    void Drop();
//    void MarkFrozen();
//}