//using EmailDB.Format;
//using Tenray.ZoneTree.AbstractFileStream;
//using EmailDB.Format.Models.BlockTypes;

//public class EmailDBFileStreamProvider : IFileStreamProvider
//{
//    private readonly SegmentManager segmentManager;
//    private readonly Dictionary<string, EmailDBFileStream> openStreams;
//    private readonly object lockObj = new();

//    public EmailDBFileStreamProvider(SegmentManager segmentManager)
//    {
//        this.segmentManager = segmentManager;
//        this.openStreams = new Dictionary<string, EmailDBFileStream>();
//    }

//    public IFileStream CreateFileStream(string path, FileMode mode, FileAccess access,
//        FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None)
//    {
//        lock (lockObj)
//        {
//            if (openStreams.TryGetValue(path, out var existingStream))
//            {
//                if (existingStream.CanBeReused(access))
//                    return existingStream;

//                existingStream.Dispose();
//                openStreams.Remove(path);
//            }

//            var stream = new EmailDBFileStream(segmentManager, path, mode, access);
//            openStreams[path] = stream;
//            return stream;
//        }
//    }

//    public void DeleteFile(string path)
//    {
//        lock (lockObj)
//        {
//            if (openStreams.TryGetValue(path, out var stream))
//            {
//                stream.Dispose();
//                openStreams.Remove(path);
//            }

//            segmentManager.DeleteSegment(path); 
           
//        }
//    }

//    public bool FileExists(string path)
//    {
//        var metadata = segmentManager.GetMetadata();
//        return metadata.SegmentOffsets.ContainsKey(path);
//    }

//    // Simple IFileStreamProvider implementation methods
//    public string CombinePaths(string path1, string path2) => Path.Combine(path1, path2);
//    public void CreateDirectory(string path) { } // No-op as we use virtual paths
//    public bool DirectoryExists(string path) => true; // Virtual paths always exist
//    public void DeleteDirectory(string path, bool recursive)
//    {
//        var metadata = segmentManager.GetMetadata();
//        var pathsToDelete = metadata.SegmentOffsets.Keys
//            .Where(p => p.StartsWith(path))
//            .ToList();

//        foreach (var p in pathsToDelete)
//        {
//            DeleteFile(p);
//        }
//    }
//    public IReadOnlyList<string> GetDirectories(string path) => new List<string>();
//    public DurableFileWriter GetDurableFileWriter() => new DurableFileWriter(this);

//    public string ReadAllText(string path)
//    {
//        throw new NotImplementedException();
//    }

//    public byte[] ReadAllBytes(string path)
//    {
//        throw new NotImplementedException();
//    }

//    public void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName)
//    {
//        throw new NotImplementedException();
//    }
//}

//public class EmailDBFileStream : IFileStream
//{
//    private readonly SegmentManager segmentManager;
//    private readonly string path;
//    private readonly FileAccess access;
//    private readonly List<SegmentContent> segments;
//    private long position;
//    private bool isDisposed;
//    private readonly object writeLock = new();

//    public EmailDBFileStream(SegmentManager segmentManager, string path,
//        FileMode mode, FileAccess access)
//    {
//        this.segmentManager = segmentManager;
//        this.path = path;
//        this.access = access;
//        this.segments = new List<SegmentContent>();

//        InitializeStream(mode);
//    }

//    private void InitializeStream(FileMode mode)
//    {
//        var metadata = segmentManager.GetMetadata();

//        // Load existing segment if it exists
//        if (metadata.SegmentOffsets.TryGetValue(path, out var offset))
//        {
//            var block = segmentManager.ReadBlock(offset);
//            if (block?.Content is SegmentContent segment)
//            {
//                segments.Add(segment);
//            }
//        }

//        switch (mode)
//        {
//            case FileMode.Create:
//            case FileMode.Truncate:
//                segments.Clear();
//                break;
//            case FileMode.CreateNew:
//                if (segments.Any())
//                    throw new IOException("File already exists");
//                break;
//            case FileMode.Open:
//                if (!segments.Any())
//                    throw new FileNotFoundException();
//                break;
//            case FileMode.Append:
//                position = segments.Sum(s => s.ContentLength);
//                break;
//        }
//    }

//    public void Write(byte[] buffer, int offset, int count)
//    {
//        if (!CanWrite)
//            throw new NotSupportedException("Stream does not support writing");

//        if (isDisposed)
//            throw new ObjectDisposedException(nameof(EmailDBFileStream));

//        lock (writeLock)
//        {
//            var segment = new SegmentContent
//            {
//                SegmentData = new byte[count],
//                FileOffset = position,
//                ContentLength = count,
//                SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
//                Version = 1
//            };

//            Buffer.BlockCopy(buffer, offset, segment.SegmentData, 0, count);

//            // Write segment and update metadata
//            var segmentOffset = segmentManager.WriteSegment(segment);
//            var metadata = segmentManager.GetMetadata();

//            // If there was a previous segment, mark it as outdated
//            if (metadata.SegmentOffsets.TryGetValue(path, out var oldOffset))
//            {
//                metadata.OutdatedOffsets.Add(oldOffset);
//            }

//            metadata.SegmentOffsets[path] = segmentOffset;
//            segmentManager.UpdateMetadata(metadata);

//            segments.Clear();
//            segments.Add(segment);
//            position += count;
//        }
//    }

//    public int Read(byte[] buffer, int offset, int count)
//    {
//        if (!CanRead)
//            throw new NotSupportedException("Stream does not support reading");

//        if (isDisposed)
//            throw new ObjectDisposedException(nameof(EmailDBFileStream));

//        if (!segments.Any() || position >= segments[0].ContentLength)
//            return 0;

//        var segment = segments[0];
//        var available = segment.ContentLength - position;
//        var toRead = Math.Min(count, available);

//        Buffer.BlockCopy(segment.SegmentData, (int)position, buffer, offset, (int)toRead);
//        position += toRead;
//        return (int)toRead;
//    }

//    // Basic IFileStream implementation
//    public bool CanRead => access.HasFlag(FileAccess.Read);
//    public bool CanWrite => access.HasFlag(FileAccess.Write);
//    public bool CanSeek => true;
//    public long Length => segments.Sum(s => s.ContentLength);
//    public long Position
//    {
//        get => position;
//        set
//        {
//            if (value < 0)
//                throw new ArgumentOutOfRangeException(nameof(value));
//            position = value;
//        }
//    }

//    public string FilePath => throw new NotImplementedException();

//    public bool CanTimeout => false;

//    public int ReadTimeout
//    {
//        get => throw new InvalidOperationException("Timeouts are not supported");
//        set => throw new InvalidOperationException("Timeouts are not supported");
//    }

//    public int WriteTimeout
//    {
//        get => throw new InvalidOperationException("Timeouts are not supported");
//        set => throw new InvalidOperationException("Timeouts are not supported");
//    }

//    public bool CanBeReused(FileAccess requiredAccess) =>
//        !isDisposed && (access == requiredAccess || access == FileAccess.ReadWrite);

//    public void Dispose()
//    {
//        if (!isDisposed)
//        {
//            isDisposed = true;
//            segments.Clear();
//        }
//    }

//    public void Flush() { }
//    public long Seek(long offset, SeekOrigin origin)
//    {
//        switch (origin)
//        {
//            case SeekOrigin.Begin:
//                Position = offset;
//                break;
//            case SeekOrigin.Current:
//                Position += offset;
//                break;
//            case SeekOrigin.End:
//                Position = Length + offset;
//                break;
//        }
//        return Position;
//    }
//    public void SetLength(long value) { }

//    public IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
//    {
//        var task = ReadAsync(buffer, offset, count);
//        if (callback != null)
//        {
//            task.ContinueWith(t => callback(t), TaskScheduler.Default);
//        }
//        return task;
//    }

//    public IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
//    {
//        var task = WriteAsync(buffer, offset, count);
//        if (callback != null)
//        {
//            task.ContinueWith(t => callback(t), TaskScheduler.Default);
//        }
//        return task;
//    }

//    public void Close()
//    {
//        Dispose();
//    }


//    public void CopyTo(Stream destination, int bufferSize)
//    {
//        if (destination == null)
//            throw new ArgumentNullException(nameof(destination));
//        if (bufferSize <= 0)
//            throw new ArgumentOutOfRangeException(nameof(bufferSize));

//        var buffer = new byte[bufferSize];
//        int read;
//        while ((read = Read(buffer, 0, buffer.Length)) != 0)
//        {
//            destination.Write(buffer, 0, read);
//        }
//    }

//    public void CopyTo(Stream destination)
//    {
//        CopyTo(destination, 81920); // Default buffer size used by Stream.CopyTo
//    }

//    public Task CopyToAsync(Stream destination)
//    {
//        return CopyToAsync(destination, 81920);
//    }

//    public Task CopyToAsync(Stream destination, int bufferSize)
//    {
//        return CopyToAsync(destination, bufferSize, CancellationToken.None);
//    }

//    public Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
//    {
//        return CopyToAsync(destination, 81920, cancellationToken);
//    }

//    public async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
//    {
//        if (destination == null)
//            throw new ArgumentNullException(nameof(destination));
//        if (bufferSize <= 0)
//            throw new ArgumentOutOfRangeException(nameof(bufferSize));

//        var buffer = new byte[bufferSize];
//        int read;
//        while ((read = await ReadAsync(buffer, 0, buffer.Length, cancellationToken)) != 0)
//        {
//            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
//        }
//    }


//    public async ValueTask DisposeAsync()
//    {
//        if (!isDisposed)
//        {
//            isDisposed = true;
//            segments.Clear();
//            GC.SuppressFinalize(this);
//        }
//        await ValueTask.CompletedTask;
//    }

//    public int EndRead(IAsyncResult asyncResult)
//    {
//        if (asyncResult is Task<int> task)
//            return task.GetAwaiter().GetResult();
//        throw new ArgumentException("Invalid IAsyncResult", nameof(asyncResult));
//    }

//    public void EndWrite(IAsyncResult asyncResult)
//    {
//        if (asyncResult is Task task)
//            task.GetAwaiter().GetResult();
//        else
//            throw new ArgumentException("Invalid IAsyncResult", nameof(asyncResult));
//    }

//    public void Flush(bool flushToDisk)
//    {
//        // No-op as writes are already handled atomically
//    }

//    public Task FlushAsync()
//    {
//        return Task.CompletedTask;
//    }

//    public Task FlushAsync(CancellationToken cancellationToken)
//    {
//        return Task.CompletedTask;
//    }

//    public int Read(Span<byte> buffer)
//    {
//        if (!CanRead)
//            throw new NotSupportedException("Stream does not support reading");

//        if (isDisposed)
//            throw new ObjectDisposedException(nameof(EmailDBFileStream));

//        if (!segments.Any() || position >= segments[0].ContentLength)
//            return 0;

//        var segment = segments[0];
//        var available = segment.ContentLength - position;
//        var toRead = Math.Min(buffer.Length, available);

//        segment.SegmentData.AsSpan((int)position, (int)toRead).CopyTo(buffer);
//        position += toRead;
//        return (int)toRead;
//    }

//    public int ReadFaster(byte[] buffer, int offset, int count)
//    {
//        return Read(buffer, offset, count); // Use standard Read implementation
//    }

//    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
//    {
//        try
//        {
//            return new ValueTask<int>(Read(buffer.Span));
//        }
//        catch (Exception ex)
//        {
//            return new ValueTask<int>(Task.FromException<int>(ex));
//        }
//    }

//    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
//    {
//        try
//        {
//            return Task.FromResult(Read(buffer, offset, count));
//        }
//        catch (Exception ex)
//        {
//            return Task.FromException<int>(ex);
//        }
//    }

//    public Task<int> ReadAsync(byte[] buffer, int offset, int count)
//    {
//        return ReadAsync(buffer, offset, count, CancellationToken.None);
//    }

//    public int ReadByte()
//    {
//        var buffer = new byte[1];
//        return Read(buffer, 0, 1) == 1 ? buffer[0] : -1;
//    }
//    public void Write(ReadOnlySpan<byte> buffer)
//    {
//        if (!CanWrite)
//            throw new NotSupportedException("Stream does not support writing");

//        if (isDisposed)
//            throw new ObjectDisposedException(nameof(EmailDBFileStream));

//        var array = buffer.ToArray();
//        Write(array, 0, array.Length);
//    }

//    public Task WriteAsync(byte[] buffer, int offset, int count)
//    {
//        return WriteAsync(buffer, offset, count, CancellationToken.None);
//    }

//    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
//    {
//        try
//        {
//            Write(buffer, offset, count);
//            return Task.CompletedTask;
//        }
//        catch (Exception ex)
//        {
//            return Task.FromException(ex);
//        }
//    }

//    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
//    {
//        try
//        {
//            Write(buffer.Span);
//            return ValueTask.CompletedTask;
//        }
//        catch (Exception ex)
//        {
//            return new ValueTask(Task.FromException(ex));
//        }
//    }

//    public void WriteByte(byte value)
//    {
//        Write(new[] { value }, 0, 1);
//    }

//    public Stream ToStream()
//    {
//        throw new NotImplementedException();
//    }
//}