using DragonHoard.InMemory;
using EmailDB.Format.Models;
using Microsoft.Extensions.Options;
using ProtoBuf;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace EmailDB.Format;

public class BlockManager : IDisposable
{
    private readonly FileStream fileStream;
    private readonly object fileLock = new object();
    private long lastWrittenPosition;
    private readonly bool enableJournaling;
    private readonly string journalPath;
    private readonly ConcurrentDictionary<long, WeakReference<Block>> blockCache;
    private readonly ConcurrentDictionary<BlockType, List<long>> blockTypeIndex;
    private readonly Timer cacheCleanupTimer;
    private bool isDisposed;

    public class BlockManagerOptions
    {
        public int MaxRetries { get; set; } = 3;
        public int BufferSize { get; set; } = 8192;
        public bool EnableJournaling { get; set; } = true;
        public TimeSpan CacheCleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
        public int MaxConcurrentOperations { get; set; } = 5;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
        public int MaxCacheSize { get; set; } = 10000;
        public bool EnableBlockTypeIndexing { get; set; } = true;
    }

    private readonly BlockManagerOptions options;
    private readonly SemaphoreSlim concurrencyLimiter;

    public BlockManager(FileStream stream, BlockManagerOptions options = null)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("Stream must support both reading and writing", nameof(stream));

        this.options = options ?? new BlockManagerOptions();
        fileStream = stream;
        enableJournaling = this.options.EnableJournaling;
        journalPath = enableJournaling ? Path.ChangeExtension(stream.Name, ".journal") : null;
        lastWrittenPosition = stream.Length;
        blockCache = new ConcurrentDictionary<long, WeakReference<Block>>();
        blockTypeIndex = new ConcurrentDictionary<BlockType, List<long>>();
        concurrencyLimiter = new SemaphoreSlim(this.options.MaxConcurrentOperations);

        if (this.options.EnableBlockTypeIndexing)
        {
            InitializeBlockTypeIndex();
        }

        cacheCleanupTimer = new Timer(CleanupCache, null,
            this.options.CacheCleanupInterval,
            this.options.CacheCleanupInterval);

        if (enableJournaling && File.Exists(journalPath))
        {
            RecoverFromJournal();
        }
    }

    private void InitializeBlockTypeIndex()
    {
        foreach (BlockType type in Enum.GetValues(typeof(BlockType)))
        {
            blockTypeIndex[type] = new List<long>();
        }

        foreach (var (offset, block) in WalkBlocks())
        {
            if (blockTypeIndex.TryGetValue(block.Header.Type, out var offsetList))
            {
                lock (offsetList)
                {
                    offsetList.Add(offset);
                }
            }
        }
    }

    public IEnumerable<(long Offset, Block Block)> GetBlocksByType(BlockType type)
    {
        if (!options.EnableBlockTypeIndexing)
            throw new InvalidOperationException("Block type indexing is disabled");

        if (blockTypeIndex.TryGetValue(type, out var offsetList))
        {
            List<long> offsets;
            lock (offsetList)
            {
                offsets = offsetList.ToList(); // Create a copy to avoid locking during iteration
            }

            foreach (var offset in offsets)
            {
                var block = ReadBlock(offset);
                if (block != null)
                {
                    yield return (offset, block);
                }
            }
        }
    }

    public async Task<long> WriteBlockAsync(Block block, long? specificOffset = null,
        CancellationToken cancellationToken = default)
    {
        if (block == null)
            throw new ArgumentNullException(nameof(block));

        if (specificOffset.HasValue && specificOffset.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(specificOffset));

        if (isDisposed)
            throw new ObjectDisposedException(nameof(BlockManager));

        await concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            for (int attempt = 0; attempt < options.MaxRetries; attempt++)
            {
                try
                {
                    return await WriteBlockInternalAsync(block, specificOffset, cancellationToken);
                }
                catch (IOException ex) when (attempt < options.MaxRetries - 1)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(options.RetryDelay.TotalMilliseconds * (attempt + 1)),
                        cancellationToken);
                }
            }

            throw new IOException($"Failed to write block after {options.MaxRetries} attempts");
        }
        finally
        {
            concurrencyLimiter.Release();
        }
    }

    private async Task<long> WriteBlockInternalAsync(Block block, long? specificOffset,
        CancellationToken cancellationToken)
    {
        lock (fileLock)
        {
            long position = specificOffset ?? lastWrittenPosition;

            if (enableJournaling)
            {
                WriteToJournal(block, position);
            }

            using (var ms = new MemoryStream())
            {
                block.Header.Checksum = 0;
                Serializer.SerializeWithLengthPrefix(ms, block, PrefixStyle.Base128);
                block.Header.Checksum = CalculateChecksum(ms.ToArray());
            }

            if (position == -1)
            {
                position = fileStream.Length;
            }

            fileStream.Position = position;

            using (var ms = new MemoryStream())
            {
                Serializer.SerializeWithLengthPrefix(ms, block, PrefixStyle.Base128);
                var data = ms.ToArray();
                fileStream.Write(data, 0, data.Length);
            }

            fileStream.Flush(true);

            if (!specificOffset.HasValue)
            {
                lastWrittenPosition = fileStream.Position;
            }

            // Update cache and index
            UpdateCacheAndIndex(position, block);

            if (enableJournaling)
            {
                ClearJournalEntry(position);
            }

            return position;
        }
    }

    private void UpdateCacheAndIndex(long position, Block block)
    {
        // Update cache with weak reference
        if (blockCache.Count >= options.MaxCacheSize)
        {
            CleanupCache(null);
        }
        blockCache[position] = new WeakReference<Block>(block);

        // Update block type index if enabled
        if (options.EnableBlockTypeIndexing &&
            blockTypeIndex.TryGetValue(block.Header.Type, out var offsetList))
        {
            lock (offsetList)
            {
                offsetList.Add(position);
            }
        }
    }

    public async Task<Block> ReadBlockAsync(long offset, CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        if (isDisposed)
            throw new ObjectDisposedException(nameof(BlockManager));

        // Try cache first
        if (TryGetFromCache(offset, out var cachedBlock))
        {
            return cachedBlock;
        }

        await concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            for (int attempt = 0; attempt < options.MaxRetries; attempt++)
            {
                try
                {
                    var block = await ReadBlockInternalAsync(offset, cancellationToken);
                    blockCache[offset] = new WeakReference<Block>(block);
                    return block;
                }
                catch (IOException ex) when (attempt < options.MaxRetries - 1)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(options.RetryDelay.TotalMilliseconds * (attempt + 1)),
                        cancellationToken);
                }
            }

            throw new IOException($"Failed to read block after {options.MaxRetries} attempts");
        }
        finally
        {
            concurrencyLimiter.Release();
        }
    }

    private bool TryGetFromCache(long offset, out Block block)
    {
        block = null;
        return blockCache.TryGetValue(offset, out var weakRef) &&
               weakRef.TryGetTarget(out block);
    }

    private async Task<Block> ReadBlockInternalAsync(long offset, CancellationToken cancellationToken)
    {
        lock (fileLock)
        {
            if (offset >= fileStream.Length)
            {
                throw new ArgumentException($"Invalid offset: {offset}. File length: {fileStream.Length}");
            }

            fileStream.Position = offset;
            Block block;

            try
            {
                block = Serializer.DeserializeWithLengthPrefix<Block>(fileStream, PrefixStyle.Base128);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Failed to deserialize block at offset {offset}", ex);
            }

            if (block == null)
            {
                throw new InvalidDataException($"Failed to deserialize block at offset {offset}");
            }

            if (!ValidateBlock(block, offset))
            {
                if (enableJournaling)
                {
                    var recoveredBlock = TryRecoverBlockFromJournal(offset);
                    if (recoveredBlock != null)
                    {
                        return recoveredBlock;
                    }
                }
                throw new InvalidDataException($"Block validation failed at offset {offset}");
            }

            return block;
        }
    }

    private bool ValidateBlock(Block block, long offset)
    {
        uint storedChecksum = block.Header.Checksum;
        block.Header.Checksum = 0;

        using (var ms = new MemoryStream())
        {
            Serializer.SerializeWithLengthPrefix(ms, block, PrefixStyle.Base128);
            uint computedChecksum = CalculateChecksum(ms.ToArray());

            // Restore original checksum
            block.Header.Checksum = storedChecksum;

            return computedChecksum == storedChecksum;
        }
    }

    public IEnumerable<(long Offset, Block Block)> WalkBlocks()
    {
        if (isDisposed)
            throw new ObjectDisposedException(nameof(BlockManager));

        long currentOffset = 0;
        int errorCount = 0;
        const int MAX_ERRORS = 5;

        while (currentOffset < fileStream.Length)
        {
            var result = TryReadBlockAt(currentOffset);
            if (result == null)
            {
                errorCount++;
                if (errorCount >= MAX_ERRORS)
                {
                    throw new InvalidOperationException(
                        $"Too many errors encountered while walking blocks. Last offset: {currentOffset}");
                }
                currentOffset += options.BufferSize;
                continue;
            }

            errorCount = 0;
            yield return (currentOffset, result.Value.Block);
            currentOffset = result.Value.NextOffset;
        }
    }

    private (Block Block, long NextOffset)? TryReadBlockAt(long offset)
    {
        try
        {
            lock (fileLock)
            {
                fileStream.Position = offset;
                Block block = Serializer.DeserializeWithLengthPrefix<Block>(fileStream, PrefixStyle.Base128);

                if (block == null)
                    return null;

                if (!ValidateBlock(block, offset))
                {
                    if (enableJournaling)
                    {
                        var recoveredBlock = TryRecoverBlockFromJournal(offset);
                        if (recoveredBlock != null)
                        {
                            return (recoveredBlock, fileStream.Position);
                        }
                    }
                    return null;
                }

                return (block, fileStream.Position);
            }
        }
        catch (Exception ex)
        {
            // Log error if needed
            return null;
        }
    }

    private void WriteToJournal(Block block, long position)
    {
        if (!enableJournaling) return;

        var journalEntry = new JournalEntry
        {
            Position = position,
            Block = block,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        using var journalStream = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.None);
        using var ms = new MemoryStream();
        Serializer.SerializeWithLengthPrefix(ms, journalEntry, PrefixStyle.Base128);
        var data = ms.ToArray();
        journalStream.Write(data, 0, data.Length);
        journalStream.Flush(true);
    }

    private void ClearJournalEntry(long position)
    {
        if (!enableJournaling || !File.Exists(journalPath)) return;

        var entries = ReadJournalEntries()
            .Where(e => e.Position != position)
            .ToList();

        if (entries.Count == 0)
        {
            File.Delete(journalPath);
            return;
        }

        using var journalStream = new FileStream(journalPath, FileMode.Create, FileAccess.Write, FileShare.None);
        foreach (var entry in entries)
        {
            using var ms = new MemoryStream();
            Serializer.SerializeWithLengthPrefix(ms, entry, PrefixStyle.Base128);
            var data = ms.ToArray();
            journalStream.Write(data, 0, data.Length);
        }
        journalStream.Flush(true);
    }

    private Block TryRecoverBlockFromJournal(long position)
    {
        if (!enableJournaling || !File.Exists(journalPath)) return null;

        var entry = ReadJournalEntries()
            .Where(e => e.Position == position)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefault();

        return entry?.Block;
    }

    private IEnumerable<JournalEntry> ReadJournalEntries()
    {
        if (!File.Exists(journalPath)) yield break;
        using var journalStream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        while (journalStream.Position < journalStream.Length)
        {
            var entry = TryReadEntry(journalStream);
            if (entry != null)
            {
                yield return entry;
            }
        }
    }

    private JournalEntry TryReadEntry(FileStream journalStream)
    {
        try
        {
            return Serializer.DeserializeWithLengthPrefix<JournalEntry>(journalStream, PrefixStyle.Base128);
        }
        catch
        {
            // Skip corrupted entries
            return null;
        }
    }

    private void RecoverFromJournal()
    {
        if (!File.Exists(journalPath)) return;

        var recoveredEntries = new List<JournalEntry>();
        foreach (var entry in ReadJournalEntries())
        {
            try
            {
                WriteBlockInternal(entry.Block, entry.Position);
                recoveredEntries.Add(entry);
            }
            catch (Exception ex)
            {
                // Log recovery failure but continue with other entries
                Console.WriteLine($"Failed to recover block at position {entry.Position}: {ex.Message}");
            }
        }

        // Clear journal after successful recovery
        if (recoveredEntries.Any())
        {
            File.Delete(journalPath);
        }
    }

    private long WriteBlockInternal(Block block, long position)
    {
        using var ms = new MemoryStream();
        block.Header.Checksum = 0;
        Serializer.SerializeWithLengthPrefix(ms, block, PrefixStyle.Base128);
        block.Header.Checksum = CalculateChecksum(ms.ToArray());

        fileStream.Position = position;
        ms.Position = 0;
        ms.CopyTo(fileStream);
        fileStream.Flush(true);

        // Update cache and index
        UpdateCacheAndIndex(position, block);

        return fileStream.Position;
    }

    private void CleanupCache(object state)
    {
        if (isDisposed) return;

        var keysToRemove = new List<long>();
        foreach (var kvp in blockCache)
        {
            if (!kvp.Value.TryGetTarget(out _))
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            blockCache.TryRemove(key, out _);
        }

        // Additional cleanup for block type index if enabled
        if (options.EnableBlockTypeIndexing)
        {
            foreach (var type in blockTypeIndex.Keys)
            {
                if (blockTypeIndex.TryGetValue(type, out var offsetList))
                {
                    lock (offsetList)
                    {
                        offsetList.RemoveAll(offset => !IsValidOffset(offset));
                    }
                }
            }
        }
    }

    private bool IsValidOffset(long offset)
    {
        try
        {
            return offset >= 0 && offset < fileStream.Length;
        }
        catch
        {
            return false;
        }
    }

    private uint CalculateChecksum(byte[] data)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(data);
        return BitConverter.ToUInt32(hash, 0);
    }

    // Synchronous versions of async methods for backward compatibility
    public long WriteBlock(Block block, long? specificOffset = null)
    {
        return WriteBlockAsync(block, specificOffset).GetAwaiter().GetResult();
    }

    public Block ReadBlock(long offset)
    {
        return ReadBlockAsync(offset).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (!isDisposed)
        {
            cacheCleanupTimer?.Dispose();
            concurrencyLimiter?.Dispose();
            blockCache.Clear();
            blockTypeIndex.Clear();
            isDisposed = true;
        }
    }

    [ProtoContract]
    private class JournalEntry
    {
        [ProtoMember(1)]
        public long Position { get; set; }

        [ProtoMember(2)]
        public Block Block { get; set; }

        [ProtoMember(3)]
        public long Timestamp { get; set; }
    }
}