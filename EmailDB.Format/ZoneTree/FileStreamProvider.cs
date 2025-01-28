using EmailDB.Format.Models;
using Tenray.ZoneTree.AbstractFileStream;

namespace EmailDB.Format.ZoneTree;

public class EmailDBFileStreamProvider : IFileStreamProvider
{
    private readonly StorageManager storageManager;
    private readonly Dictionary<string, EmailDBFileStream> openStreams;
    private readonly object lockObj = new();

    public EmailDBFileStreamProvider(StorageManager storageManager)
    {
        this.storageManager = storageManager;
        this.openStreams = new Dictionary<string, EmailDBFileStream>();
    }

    public string CombinePaths(string path1, string path2)
    {
        return path1 + "/" + path2;
    }

    public void CreateDirectory(string path)
    {
        // No-op as we don't need physical directories
    }

    public bool DirectoryExists(string path)
    {
        return true; // Always return true as we don't use physical directories
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        lock (lockObj)
        {
            // Close streams in this directory
            var streamsToRemove = openStreams
                .Where(kvp => kvp.Key.StartsWith(path))
                .ToList();

            foreach (var stream in streamsToRemove)
            {
                stream.Value.Dispose();
                openStreams.Remove(stream.Key);
            }

            // Mark segments as deleted
            var segments = GetSegmentsForPath(path, true);
            if (segments.Any())
            {
                storageManager.UpdateMetadata(metadata =>
                {
                    metadata.OutdatedOffsets.AddRange(segments.Select(s => s.offset));
                    return metadata;
                });
            }
        }
    }

    public IFileStream CreateFileStream(string path, FileMode mode, FileAccess access,
        FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None)
    {
        lock (lockObj)
        {
            if (openStreams.TryGetValue(path, out var existingStream))
            {
                if (existingStream.CanBeReused(access))
                    return existingStream;

                existingStream.Dispose();
                openStreams.Remove(path);
            }

            var stream = new EmailDBFileStream(storageManager, path, mode, access);
            openStreams[path] = stream;
            return stream;
        }
    }

    public void DeleteFile(string path)
    {
        lock (lockObj)
        {
            if (openStreams.TryGetValue(path, out var stream))
            {
                stream.Dispose();
                openStreams.Remove(path);
            }

            // Mark segments as deleted in metadata
            var segments = GetSegmentsForPath(path);
            if (segments.Any())
            {
                storageManager.UpdateMetadata(metadata =>
                {
                    metadata.OutdatedOffsets.AddRange(segments.Select(s => s.offset));
                    return metadata;
                });
            }
        }
    }

    public bool FileExists(string path)
    {
        return GetSegmentsForPath(path).Any();
    }

    public IReadOnlyList<string> GetDirectories(string path)
    {
        return new List<string>(); // Return empty list as we don't use physical directories
    }

    public DurableFileWriter GetDurableFileWriter()
    {
        return new DurableFileWriter(this);
    }

    public byte[] ReadAllBytes(string path)
    {
        var segments = GetSegmentsForPath(path);
        if (!segments.Any())
            throw new FileNotFoundException($"No data found for path: {path}");

        using var ms = new MemoryStream();
        foreach (var segment in segments.OrderBy(s => s.content.FileOffset))
        {
            ms.Write(segment.content.SegmentData);
        }
        return ms.ToArray();
    }

    public string ReadAllText(string path)
    {
        return System.Text.Encoding.UTF8.GetString(ReadAllBytes(path));
    }

    public void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName)
    {
        lock (lockObj)
        {
            // Close affected streams
            CloseStreams(sourceFileName, destinationFileName, destinationBackupFileName);

            // Backup if needed
            if (destinationBackupFileName != null && FileExists(destinationFileName))
            {
                var backupSegments = GetSegmentsForPath(destinationFileName)
                    .Select(s => s.content)
                    .ToList();

                foreach (var segment in backupSegments)
                {
                    segment.FileName = destinationBackupFileName;
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
                    storageManager.WriteBlock(block);
                }
            }

            // Replace destination with source
            var sourceSegments = GetSegmentsForPath(sourceFileName)
                .Select(s => s.content)
                .ToList();

            // Mark old destination segments as outdated
            var oldDestSegments = GetSegmentsForPath(destinationFileName);
            storageManager.UpdateMetadata(metadata =>
            {
                metadata.OutdatedOffsets.AddRange(oldDestSegments.Select(s => s.offset));
                return metadata;
            });

            // Write new segments
            foreach (var segment in sourceSegments)
            {
                segment.FileName = destinationFileName;
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
                storageManager.WriteBlock(block);
            }

            // Mark source segments as outdated
            storageManager.UpdateMetadata(metadata =>
            {
                metadata.OutdatedOffsets.AddRange(
                    GetSegmentsForPath(sourceFileName).Select(s => s.offset));
                return metadata;
            });
        }
    }

    private IEnumerable<(long offset, SegmentContent content)> GetSegmentsForPath(string path, bool includeSubPaths = false)
    {
        var segments = new List<(long, SegmentContent)>();
        foreach (var (offset, block) in storageManager.WalkBlocks())
        {
            if (block.Content is SegmentContent segment)
            {
                var segmentPath = segment.FileName;
                if (segmentPath == path || (includeSubPaths && segmentPath.StartsWith(path)))
                {
                    segments.Add((offset, segment));
                }
            }
        }
        return segments;
    }

    private void CloseStreams(params string[] paths)
    {
        foreach (var path in paths.Where(p => p != null))
        {
            if (openStreams.TryGetValue(path, out var stream))
            {
                stream.Dispose();
                openStreams.Remove(path);
            }
        }
    }
}


public class EmailDBFileStream : IFileStream
{
    private readonly StorageManager storageManager;
    private readonly string path;
    private readonly FileAccess access;
    private readonly List<SegmentContent> segments;
    private long position;
    private bool isDisposed;
    private readonly object writeLock = new();

    public EmailDBFileStream(StorageManager storageManager, string path,
        FileMode mode, FileAccess access)
    {
        this.storageManager = storageManager;
        this.path = path;
        this.access = access;
        this.segments = new List<SegmentContent>();

        InitializeStream(mode);
    }

    private void InitializeStream(FileMode mode)
    {
        // Load existing segments
        foreach (var (_, block) in storageManager.WalkBlocks())
        {
            if (block.Content is SegmentContent segment && segment.FileName == path)
            {
                segments.Add(segment);
            }
        }
        segments.Sort((a, b) => a.FileOffset.CompareTo(b.FileOffset));

        // Handle different FileMode options
        switch (mode)
        {
            case FileMode.Create:
            case FileMode.Truncate:
                segments.Clear();
                break;
            case FileMode.CreateNew:
                if (segments.Any())
                    throw new IOException("File already exists");
                break;
            case FileMode.Open:
                if (!segments.Any())
                    throw new FileNotFoundException();
                break;
            case FileMode.OpenOrCreate:
                // Do nothing - either way is fine
                break;
            case FileMode.Append:
                position = segments.Sum(s => s.ContentLength);
                break;
        }
    }

    public bool CanRead => access.HasFlag(FileAccess.Read);
    public bool CanWrite => access.HasFlag(FileAccess.Write);
    public bool CanSeek => true;
    public bool CanTimeout => false;
    public string FilePath => path;
    public string Name => path;

    public long Length => segments.Sum(s => s.ContentLength);

    public long Position
    {
        get => position;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            position = value;
        }
    }

    public int ReadTimeout
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public int WriteTimeout
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public bool CanBeReused(FileAccess requiredAccess)
    {
        return !isDisposed && (access == requiredAccess || access == FileAccess.ReadWrite);
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (!CanWrite)
            throw new NotSupportedException("Stream does not support writing");

        if (isDisposed)
            throw new ObjectDisposedException(nameof(EmailDBFileStream));

        lock (writeLock)
        {
            var segment = new SegmentContent
            {
                SegmentId = GetNextSegmentId(),
                SegmentData = new byte[count],
                FileName = path,
                FileOffset = position,
                ContentLength = count,
                SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            };

            Buffer.BlockCopy(buffer, offset, segment.SegmentData, 0, count);

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

            storageManager.WriteBlock(block);
            segments.Add(segment);
            position += count;
        }
    }

    public void Write(ReadOnlySpan<byte> buffer)
    {
        var array = buffer.ToArray();
        Write(array, 0, array.Length);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (!CanRead)
            throw new NotSupportedException("Stream does not support reading");

        if (isDisposed)
            throw new ObjectDisposedException(nameof(EmailDBFileStream));

        var totalBytesRead = 0;
        var remainingCount = count;
        var currentPosition = position;

        while (remainingCount > 0 && currentPosition < Length)
        {
            var (segment, segmentOffset) = FindSegmentForPosition(currentPosition);
            if (segment == null)
                break;

            var bytesAvailable = segment.ContentLength - segmentOffset;
            var bytesToRead = Math.Min(remainingCount, bytesAvailable);

            Buffer.BlockCopy(segment.SegmentData, segmentOffset,
                buffer, offset + totalBytesRead, bytesToRead);

            totalBytesRead += bytesToRead;
            remainingCount -= bytesToRead;
            currentPosition += bytesToRead;
        }

        position = currentPosition;
        return totalBytesRead;
    }

    public int Read(Span<byte> buffer)
    {
        var array = new byte[buffer.Length];
        var bytesRead = Read(array, 0, array.Length);
        if (bytesRead > 0)
            array.AsSpan(0, bytesRead).CopyTo(buffer);
        return bytesRead;
    }

    public long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin:
                Position = offset;
                break;
            case SeekOrigin.Current:
                Position += offset;
                break;
            case SeekOrigin.End:
                Position = Length + offset;
                break;
        }
        return Position;
    }

    public void SetLength(long value)
    {
        if (!CanWrite)
            throw new NotSupportedException();

        lock (writeLock)
        {
            var currentLength = Length;
            if (value == currentLength)
                return;

            if (value < currentLength)
            {
                // Truncate by marking segments as outdated
                var (segment, _) = FindSegmentForPosition(value);
                if (segment != null)
                {
                    var index = segments.IndexOf(segment);
                    var segmentsToRemove = segments.Skip(index).ToList();
                    segments.RemoveRange(index, segments.Count - index);

                    storageManager.UpdateMetadata(metadata =>
                    {
                        foreach (var seg in segmentsToRemove)
                        {
                            metadata.OutdatedOffsets.AddRange(
                                from block in storageManager.WalkBlocks()
                                where block.Block.Content is SegmentContent content
                                    && content.SegmentId == seg.SegmentId
                                select block.Item1);
                        }
                        return metadata;
                    });
                }
            }
            // If value > currentLength, we'll let the space be filled with writes
        }
    }

    public void Flush()
    {
        // No-op as we write immediately
    }

    public void Flush(bool flushToDisk)
    {
        // No-op as we write immediately
    }

    private (SegmentContent segment, int offset) FindSegmentForPosition(long pos)
    {
        long currentOffset = 0;
        foreach (var segment in segments)
        {
            var nextOffset = currentOffset + segment.ContentLength;
            if (currentOffset <= pos && pos < nextOffset)
            {
                return (segment, (int)(pos - currentOffset));
            }
            currentOffset = nextOffset;
        }
        return (null, 0);
    }

    private ulong GetNextSegmentId()
    {
        return segments.Any() ? segments.Max(s => s.SegmentId) + 1 : 0;
    }

    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Write(buffer, offset, count), cancellationToken);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await WriteAsync(buffer.ToArray(), 0, buffer.Length, cancellationToken);
    }

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => Read(buffer, offset, count), cancellationToken);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var array = new byte[buffer.Length];
        var bytesRead = await ReadAsync(array, 0, array.Length, cancellationToken);
        if (bytesRead > 0)
            array.AsMemory(0, bytesRead).CopyTo(buffer);
        return bytesRead;
    }

    public int ReadFaster(byte[] buffer, int offset, int count)
    {
        return Read(buffer, offset, count);
    }

    public int ReadByte()
    {
        var buffer = new byte[1];
        return Read(buffer, 0, 1) == 1 ? buffer[0] : -1;
    }

    public void WriteByte(byte value)
    {
        Write(new[] { value }, 0, 1);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // No-op as we write immediately
        await Task.CompletedTask;
    }

    public void Close()
    {
        Dispose();
    }
    public void CopyTo(Stream destination)
    {
        CopyTo(destination, 81920);
    }

    public void CopyTo(Stream destination, int bufferSize = 81920)
    {
        var buffer = new byte[bufferSize];
        int read;
        while ((read = Read(buffer, 0, buffer.Length)) != 0)
        {
            destination.Write(buffer, 0, read);
        }
    }
    public Task CopyToAsync(Stream destination)
    {
        return CopyToAsync(destination, 81920, CancellationToken.None);
    }

    public Task CopyToAsync(Stream destination, int bufferSize)
    {
        return CopyToAsync(destination, bufferSize, CancellationToken.None);
    }

    public Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        return CopyToAsync(destination, 81920, cancellationToken);
    }   

    public async Task CopyToAsync(Stream destination, int bufferSize = 81920, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[bufferSize];
        int read;
        while ((read = await ReadAsync(buffer, 0, buffer.Length, cancellationToken)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    public Stream ToStream()
    {
        throw new NotSupportedException("Direct stream conversion not supported");
    }

    public IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
    {
        return Task.Factory.FromAsync(
            (asyncCallback, asyncState) =>
                new Task<int>(() => Read(buffer, offset, count)),
             ar => callback?.Invoke(ar),
            state);
    }

    public IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
    {
        return Task.Factory.FromAsync(
            (asyncCallback, asyncState) =>
                new Task(() => Write(buffer, offset, count)),
             ar => callback?.Invoke(ar),
            state);
    }

    public int EndRead(IAsyncResult asyncResult)
    {
        return ((Task<int>)asyncResult).Result;
    }

    public void EndWrite(IAsyncResult asyncResult)
    {
        ((Task)asyncResult).Wait();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!isDisposed)
        {
            isDisposed = true;
            segments.Clear();
        }
    }


  
  

    public Task FlushAsync()
    {
        // No-op as we write immediately
        return Task.CompletedTask;
    }

    public Task<int> ReadAsync(byte[] buffer, int offset, int count)
    {
        return Task.FromResult(Read(buffer, offset, count));
    }

    public Task WriteAsync(byte[] buffer, int offset, int count)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

   
}