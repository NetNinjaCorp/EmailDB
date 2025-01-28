using EmailDB.Format.Models;
using ProtoBuf;
using System.Collections.Concurrent;
using Tenray.ZoneTree.AbstractFileStream;
using Tenray.ZoneTree.Exceptions.WAL;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Segments.Block;
using Tenray.ZoneTree.Segments.RandomAccess;
using Tenray.ZoneTree.WAL;

namespace EmailDB.Format.ZoneTree;

/// <summary>
/// RandomAccessDevice implementation using EmailDB storage
/// </summary>
public class RandomAccessDevice : IRandomAccessDevice
{
    private readonly StorageManager storageManager;
    private readonly string name;
    private readonly long segmentId;
    private readonly string category;
    private readonly string folderName;
    private long position;
    private bool isDropped;
    private readonly ConcurrentDictionary<long, byte[]> dataCache;
    private readonly object writeLock = new object();

    public RandomAccessDevice(StorageManager storageManager, string name, long segmentId, string category, bool writable)
    {
        this.storageManager = storageManager;
        this.name = name;
        this.segmentId = segmentId;
        this.category = category;
        this.folderName = $"{name}_{category}_{segmentId}";
        Writable = writable;
        dataCache = new ConcurrentDictionary<long, byte[]>();

        // Create folder if writable
        if (writable)
        {
            storageManager.CreateFolder(folderName);
        }
    }

    public long SegmentId => segmentId;
    public bool Writable { get; private set; }
    public int ReadBufferCount => dataCache.Count;
    public long Length => GetLength();

    private long GetLength()
    {
        var folder = GetDeviceFolder();
        if (folder == null) return 0;

        long totalLength = 0;
        foreach (var emailId in folder.EmailIds)
        {
            var segment = ReadSegmentById(emailId);
            if (segment != null)
            {
                totalLength += segment.SegmentData.Length;
            }
        }
        return totalLength;
    }

    public long AppendBytesReturnPosition(Memory<byte> bytes)
    {
        if (!Writable)
            throw new InvalidOperationException("Device is not writable");

        if (isDropped)
            throw new ObjectDisposedException(nameof(RandomAccessDevice));

        lock (writeLock)
        {
            var currentPosition = position;

            // Create and store segment
            var segment = new SegmentContent
            {
                SegmentId = GetNextSegmentId(),
                SegmentData = bytes.ToArray(),
                FileOffset = currentPosition,
                ContentLength = bytes.Length,
                SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            };

            StoreSegment(segment);
            dataCache[currentPosition] = bytes.ToArray();
            position += bytes.Length;

            return currentPosition;
        }
    }

    public Memory<byte> GetBytes(long offset, int length, SingleBlockPin blockPin = null)
    {
        if (isDropped)
            throw new ObjectDisposedException(nameof(RandomAccessDevice));

        // Try cache first
        if (dataCache.TryGetValue(offset, out var cachedData))
        {
            return new Memory<byte>(cachedData, 0, Math.Min(length, cachedData.Length));
        }

        // Find segment containing the requested range
        var folder = GetDeviceFolder();
        if (folder == null)
            return Memory<byte>.Empty;

        long currentOffset = 0;
        foreach (var emailId in folder.EmailIds)
        {
            var segment = ReadSegmentById(emailId);
            if (segment == null) continue;

            var segmentLength = segment.SegmentData.Length;

            if (currentOffset <= offset && offset < currentOffset + segmentLength)
            {
                var segmentOffset = (int)(offset - currentOffset);
                var available = segmentLength - segmentOffset;
                var bytesToRead = Math.Min(length, available);

                // Cache the data
                dataCache[offset] = segment.SegmentData;

                return new Memory<byte>(segment.SegmentData, segmentOffset, bytesToRead);
            }

            currentOffset += segmentLength;
        }

        return Memory<byte>.Empty;
    }

    public void ClearContent()
    {
        if (!Writable)
            throw new InvalidOperationException("Device is not writable");

        if (isDropped)
            throw new ObjectDisposedException(nameof(RandomAccessDevice));

        lock (writeLock)
        {
            storageManager.DeleteFolder(folderName, true);
            storageManager.CreateFolder(folderName);
            position = 0;
            dataCache.Clear();
        }
    }

    public void Close()
    {
        dataCache.Clear();
    }

    public void Delete()
    {
        if (isDropped) return;

        lock (writeLock)
        {
            storageManager.DeleteFolder(folderName, true);
            isDropped = true;
            dataCache.Clear();
        }
    }

    public void Drop()
    {
        Delete();
    }

    public void SealDevice()
    {
        Writable = false;
    }

    public int ReleaseInactiveCachedBuffers(long ticks)
    {
        var removed = 0;
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ticks;

        foreach (var key in dataCache.Keys)
        {
            if (dataCache.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private FolderContent GetDeviceFolder()
    {
        foreach (var (_, block) in storageManager.WalkBlocks())
        {
            if (block.Content is FolderContent folder && folder.Name == folderName)
            {
                return folder;
            }
        }
        return null;
    }

    private ulong GetNextSegmentId()
    {
        var folder = GetDeviceFolder();
        return folder?.EmailIds.Count > 0 ? folder.EmailIds.Max() + 1 : 0;
    }

    private SegmentContent ReadSegmentById(ulong id)
    {
        foreach (var (_, block) in storageManager.WalkBlocks())
        {
            if (block.Content is SegmentContent segment && segment.SegmentId == id)
            {
                return segment;
            }
        }
        return null;
    }

    private void StoreSegment(SegmentContent segment)
    {
        var block = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.Segment,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = segment
        };

        using var ms = new MemoryStream();
        Serializer.SerializeWithLengthPrefix(ms, block, PrefixStyle.Base128);
        storageManager.AddEmailToFolder(folderName, ms.ToArray());
    }

    public void Dispose()
    {
        if (!isDropped)
        {
            Close();
            isDropped = true;
        }
    }
}

