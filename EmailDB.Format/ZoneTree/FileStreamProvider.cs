using EmailDB.Format.FileManagement;
using Tenray.ZoneTree.AbstractFileStream;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Format.ZoneTree;

public class EmailDBFileStreamProvider : IFileStreamProvider
{
    private readonly RawBlockManager blockManager;
    private readonly Dictionary<string, EmailDBFileStream> openStreams;
    private readonly object lockObj = new();

    public EmailDBFileStreamProvider(RawBlockManager blockManager)
    {
        this.blockManager = blockManager;
        this.openStreams = new Dictionary<string, EmailDBFileStream>();
    }

    public IFileStream CreateFileStream(string path, FileMode mode, FileAccess access,
        FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None)
    {
        lock (lockObj)
        {
            var pathHash = path.GetHashCode();
            ZoneTreeLogger.LogFileOperation("CreateFileStream", path, $"Mode: {mode}, Access: {access}, Share: {share}");
            
            // For metadata files, if mode is CreateNew and file exists, we should use OpenOrCreate mode instead
            // This handles the case where ZoneTree is reopening an existing database
            // ZoneTree metadata files typically include "meta.json" or segment-related patterns
            if (mode == FileMode.CreateNew)
            {
                var locations = blockManager.GetBlockLocations();
                ZoneTreeLogger.Log($"Checking block storage for existing file: locations.Count={locations.Count}");
                if (locations.ContainsKey(pathHash))
                {
                    ZoneTreeLogger.LogFileOperation("FileExists", path, "Switching from CreateNew to OpenOrCreate");
                    mode = FileMode.OpenOrCreate;
                }
                else
                {
                    ZoneTreeLogger.LogFileOperation("FileNotFound", path, "Will create new file");
                }
            }
            
            if (openStreams.TryGetValue(path, out var existingStream))
            {
                if (existingStream.CanBeReused(access))
                {
                    Console.WriteLine($"   Reusing existing stream for {path}");
                    return existingStream;
                }

                Console.WriteLine($"   Disposing existing stream for {path} before creating new one");
                existingStream.Dispose();
                openStreams.Remove(path);
            }

            var stream = new EmailDBFileStream(blockManager, path, mode, access);
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

            // Mark blocks as deleted by their path-based ID
            var pathHash = path.GetHashCode();
            var locations = blockManager.GetBlockLocations();
            if (locations.ContainsKey(pathHash))
            {
                // Note: RawBlockManager doesn't have a delete method, but we could mark it
                // For now, just remove from our tracking
            }
        }
    }

    public bool FileExists(string path)
    {
        var pathHash = path.GetHashCode();
        var locations = blockManager.GetBlockLocations();
        var exists = locations.ContainsKey(pathHash);
        Console.WriteLine($"🔍 FileExists check: path={path}, hash={pathHash}, exists={exists}");
        return exists;
    }

    // Simple IFileStreamProvider implementation methods
    public string CombinePaths(string path1, string path2) => Path.Combine(path1, path2);
    public void CreateDirectory(string path) { } // No-op as we use virtual paths
    public bool DirectoryExists(string path) => true; // Virtual paths always exist
    public void DeleteDirectory(string path, bool recursive)
    {
        var locations = blockManager.GetBlockLocations();
        var pathsToDelete = locations.Keys
            .Where(blockId => blockId.ToString().StartsWith(path.GetHashCode().ToString()))
            .ToList();

        foreach (var blockId in pathsToDelete)
        {
            // Mark for deletion - in a real implementation we'd need proper deletion
        }
    }
    public IReadOnlyList<string> GetDirectories(string path) => new List<string>();
    public DurableFileWriter GetDurableFileWriter() => new DurableFileWriter(this);

    public string ReadAllText(string path)
    {
        var pathHash = path.GetHashCode();
        Console.WriteLine($"📖 ReadAllText: path={path}, hash={pathHash}");
        var readResult = blockManager.ReadBlockAsync(pathHash).Result;
        if (readResult.IsSuccess)
        {
            var text = System.Text.Encoding.UTF8.GetString(readResult.Value.Payload);
            Console.WriteLine($"✅ ReadAllText success: {text.Length} chars");
            if (path.Contains("meta") && text.Length < 1000)
            {
                Console.WriteLine($"   Content: {text}");
            }
            return text;
        }
        Console.WriteLine($"❌ ReadAllText failed: File not found");
        throw new FileNotFoundException($"File not found: {path}");
    }

    public byte[] ReadAllBytes(string path)
    {
        var pathHash = path.GetHashCode();
        var readResult = blockManager.ReadBlockAsync(pathHash).Result;
        if (readResult.IsSuccess)
        {
            return readResult.Value.Payload;
        }
        throw new FileNotFoundException($"File not found: {path}");
    }

    public void Replace(string sourceFileName, string destinationFileName, string destinationBackupFileName)
    {
        // ZoneTree uses Replace for atomic file updates (e.g., metadata files)
        // We need to:
        // 1. Read the source file content
        // 2. Optionally backup the destination if it exists
        // 3. Write the source content to the destination
        // 4. Delete the source file
        
        ZoneTreeLogger.LogFileOperation("Replace", sourceFileName, $"Destination: {destinationFileName}, Backup: {destinationBackupFileName ?? "none"}");
        
        try
        {
            // First, ensure any open stream for the source file is flushed and closed
            lock (lockObj)
            {
                if (openStreams.TryGetValue(sourceFileName, out var sourceStream))
                {
                    ZoneTreeLogger.Log("Flushing and closing source stream before replace");
                    sourceStream.Dispose();
                    openStreams.Remove(sourceFileName);
                }
            }
            
            // Read source content
            var sourceHash = sourceFileName.GetHashCode();
            Console.WriteLine($"   Reading source file with hash={sourceHash}");
            var sourceResult = blockManager.ReadBlockAsync(sourceHash).Result;
            if (!sourceResult.IsSuccess)
            {
                Console.WriteLine($"   ❌ Source file not found in block storage");
                throw new FileNotFoundException($"Source file not found: {sourceFileName}");
            }
            Console.WriteLine($"   ✅ Read {sourceResult.Value.Payload.Length} bytes from source file");
            
            var sourceContent = sourceResult.Value.Payload;
            
            // If destination exists and backup is requested, copy it to backup
            if (!string.IsNullOrEmpty(destinationBackupFileName))
            {
                var destHash = destinationFileName.GetHashCode();
                var destResult = blockManager.ReadBlockAsync(destHash).Result;
                if (destResult.IsSuccess)
                {
                    // Write backup
                    var backupBlock = new Block
                    {
                        Version = 1,
                        Type = BlockType.ZoneTreeSegment_KV,
                        Flags = 0,
                        Encoding = PayloadEncoding.RawBytes,
                        Timestamp = DateTime.UtcNow.Ticks,
                        BlockId = destinationBackupFileName.GetHashCode(),
                        Payload = destResult.Value.Payload
                    };
                    blockManager.WriteBlockAsync(backupBlock).Wait();
                }
            }
            
            // Write source content to destination
            var destBlock = new Block
            {
                Version = 1,
                Type = BlockType.ZoneTreeSegment_KV,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = destinationFileName.GetHashCode(),
                Payload = sourceContent
            };
            
            var writeResult = blockManager.WriteBlockAsync(destBlock).Result;
            if (!writeResult.IsSuccess)
            {
                throw new IOException($"Failed to write destination file: {destinationFileName}");
            }
            
            // Note: We don't actually delete the source block as RawBlockManager doesn't support deletion
            // This is OK for ZoneTree's purposes as it won't reference the old file anymore
            
            Console.WriteLine($"✅ Replace completed: {sourceFileName} -> {destinationFileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Replace failed: {ex.Message}");
            throw;
        }
    }
}

public class EmailDBFileStream : IFileStream
{
    private readonly RawBlockManager blockManager;
    private readonly string path;
    private readonly FileAccess access;
    private readonly int pathHash;
    private byte[] data;
    private long position;
    private bool isDisposed;
    private bool isDirty;
    private readonly object writeLock = new();

    public EmailDBFileStream(RawBlockManager blockManager, string path,
        FileMode mode, FileAccess access)
    {
        this.blockManager = blockManager;
        this.path = path;
        this.access = access;
        this.pathHash = path.GetHashCode();
        this.data = Array.Empty<byte>();

        Console.WriteLine($"🔍 EmailDBFileStream created: path={path}, mode={mode}, access={access}");
        InitializeStream(mode);
    }

    private void InitializeStream(FileMode mode)
    {
        // Try to load existing data
        var readResult = blockManager.ReadBlockAsync(pathHash).Result;
        var hasExistingData = readResult.IsSuccess;

        if (hasExistingData)
        {
            data = readResult.Value.Payload;
            ZoneTreeLogger.LogBlockOperation("ReadExisting", pathHash, data.Length, $"Loaded for {path}");
            ZoneTreeLogger.LogData($"{path} content", data);
        }
        else
        {
            ZoneTreeLogger.LogBlockOperation("ReadMissing", pathHash, 0, $"No existing data for {path}");
        }

        switch (mode)
        {
            case FileMode.Create:
            case FileMode.Truncate:
                data = Array.Empty<byte>();
                isDirty = true;
                break;
            case FileMode.CreateNew:
                if (hasExistingData)
                {
                    // CreateNew should fail if file exists
                    throw new IOException($"File already exists: {path}");
                }
                else
                {
                    data = Array.Empty<byte>();
                    isDirty = true;
                }
                break;
            case FileMode.OpenOrCreate:
                // If file doesn't exist, create it
                if (!hasExistingData)
                {
                    data = Array.Empty<byte>();
                    isDirty = true;
                }
                break;
            case FileMode.Open:
                if (!hasExistingData)
                    throw new FileNotFoundException();
                break;
            case FileMode.Append:
                position = data.Length;
                break;
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (!CanWrite)
            throw new NotSupportedException("Stream does not support writing");

        if (isDisposed)
            throw new ObjectDisposedException(nameof(EmailDBFileStream));

        lock (writeLock)
        {
            // Extend data array if needed
            var newLength = Math.Max(data.Length, position + count);
            if (newLength > data.Length)
            {
                var newData = new byte[newLength];
                data.CopyTo(newData, 0);
                data = newData;
            }

            // Write the data
            Buffer.BlockCopy(buffer, offset, data, (int)position, count);
            position += count;
            isDirty = true;
            
            // Debug: Log all writes to metadata files
            if (path.EndsWith(".json_0") || path.EndsWith(".json_0.tmp"))
            {
                ZoneTreeLogger.LogFileOperation("Write", path, $"Position: {position - count}, Count: {count}, IsDirty: {isDirty}");
                
                // Log the actual data being written for analysis
                var writtenData = new byte[count];
                Buffer.BlockCopy(buffer, offset, writtenData, 0, count);
                ZoneTreeLogger.LogData($"Written to {path}", writtenData);
            }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (!CanRead)
            throw new NotSupportedException("Stream does not support reading");

        if (isDisposed)
            throw new ObjectDisposedException(nameof(EmailDBFileStream));

        if (position >= data.Length)
            return 0;

        var available = data.Length - position;
        var toRead = Math.Min(count, available);

        Buffer.BlockCopy(data, (int)position, buffer, offset, (int)toRead);
        position += toRead;
        
        // Debug: Log metadata reads
        if ((path.Contains("meta") || path.Contains("wal") || path.EndsWith(".json_0")) && toRead > 0)
        {
            ZoneTreeLogger.LogFileOperation("Read", path, $"Position: {position-toRead}, Count: {toRead}, Total: {data.Length}");
            
            // Log the data being read
            var readData = new byte[toRead];
            Buffer.BlockCopy(buffer, offset, readData, 0, (int)toRead);
            ZoneTreeLogger.LogData($"Read from {path}", readData);
            
            if (data.Length == 0)
            {
                ZoneTreeLogger.Log($"⚠️ WARNING: File {path} is empty!");
            }
            else
            {
                // Show hex dump of first 100 bytes to understand the format
                var hexDump = BitConverter.ToString(data.Take(Math.Min(100, data.Length)).ToArray()).Replace("-", " ");
                Console.WriteLine($"   Hex dump: {hexDump}");
            }
        }
        
        return (int)toRead;
    }

    public void Flush()
    {
        if (isDirty && CanWrite)
        {
            ZoneTreeLogger.LogFileOperation("Flush", path, $"DataLength: {data.Length}");
            ZoneTreeLogger.LogData($"Flushing {path}", data);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.ZoneTreeSegment_KV,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = pathHash,
                Payload = data
            };

            var result = blockManager.WriteBlockAsync(block).Result;
            if (result.IsSuccess)
            {
                ZoneTreeLogger.LogBlockOperation("FlushSuccess", pathHash, data.Length, $"Flushed {path}");
                isDirty = false;
            }
            else
            {
                ZoneTreeLogger.LogBlockOperation("FlushFailed", pathHash, data.Length, $"Failed to flush {path}");
            }
        }
    }

    // Basic IFileStream implementation
    public bool CanRead => access.HasFlag(FileAccess.Read);
    public bool CanWrite => access.HasFlag(FileAccess.Write);
    public bool CanSeek => true;
    public long Length => data.Length;
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

    public string FilePath => path;
    public bool CanTimeout => false;

    public int ReadTimeout
    {
        get => throw new InvalidOperationException("Timeouts are not supported");
        set => throw new InvalidOperationException("Timeouts are not supported");
    }

    public int WriteTimeout
    {
        get => throw new InvalidOperationException("Timeouts are not supported");
        set => throw new InvalidOperationException("Timeouts are not supported");
    }

    public bool CanBeReused(FileAccess requiredAccess) =>
        !isDisposed && (access == requiredAccess || access == FileAccess.ReadWrite);

    public void Dispose()
    {
        if (!isDisposed)
        {
            Flush(); // Save any pending changes
            isDisposed = true;
            data = Array.Empty<byte>();
        }
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
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));

        if (value != data.Length)
        {
            var newData = new byte[value];
            if (data.Length > 0)
            {
                var copyLength = Math.Min(data.Length, (int)value);
                Buffer.BlockCopy(data, 0, newData, 0, copyLength);
            }
            data = newData;
            isDirty = true;

            if (position > value)
                position = value;
        }
    }

    // Async support methods
    public IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
    {
        var task = Task.FromResult(Read(buffer, offset, count));
        if (callback != null)
            task.ContinueWith(t => callback(t), TaskScheduler.Default);
        return task;
    }

    public IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
    {
        var task = Task.Run(() => Write(buffer, offset, count));
        if (callback != null)
            task.ContinueWith(t => callback(t), TaskScheduler.Default);
        return task;
    }

    public void Close() => Dispose();

    public void CopyTo(Stream destination, int bufferSize)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        var buffer = new byte[bufferSize];
        int read;
        while ((read = Read(buffer, 0, buffer.Length)) != 0)
        {
            destination.Write(buffer, 0, read);
        }
    }

    public void CopyTo(Stream destination) => CopyTo(destination, 81920);

    public Task CopyToAsync(Stream destination) => CopyToAsync(destination, 81920);
    public Task CopyToAsync(Stream destination, int bufferSize) => CopyToAsync(destination, bufferSize, CancellationToken.None);
    public Task CopyToAsync(Stream destination, CancellationToken cancellationToken) => CopyToAsync(destination, 81920, cancellationToken);

    public async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        var buffer = new byte[bufferSize];
        int read;
        while ((read = await ReadAsync(buffer, 0, buffer.Length, cancellationToken)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!isDisposed)
        {
            await Task.Run(Flush);
            isDisposed = true;
            data = Array.Empty<byte>();
            GC.SuppressFinalize(this);
        }
    }

    public int EndRead(IAsyncResult asyncResult)
    {
        if (asyncResult is Task<int> task)
            return task.GetAwaiter().GetResult();
        throw new ArgumentException("Invalid IAsyncResult", nameof(asyncResult));
    }

    public void EndWrite(IAsyncResult asyncResult)
    {
        if (asyncResult is Task task)
            task.GetAwaiter().GetResult();
        else
            throw new ArgumentException("Invalid IAsyncResult", nameof(asyncResult));
    }

    public void Flush(bool flushToDisk) => Flush();
    public Task FlushAsync() => Task.Run(Flush);
    public Task FlushAsync(CancellationToken cancellationToken) => Task.Run(Flush, cancellationToken);

    public int Read(Span<byte> buffer)
    {
        if (!CanRead)
            throw new NotSupportedException("Stream does not support reading");

        if (position >= data.Length)
            return 0;

        var available = data.Length - position;
        var toRead = Math.Min(buffer.Length, available);

        data.AsSpan((int)position, (int)toRead).CopyTo(buffer);
        position += toRead;
        return (int)toRead;
    }

    public int ReadFaster(byte[] buffer, int offset, int count) => Read(buffer, offset, count);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return new ValueTask<int>(Read(buffer.Span));
        }
        catch (Exception ex)
        {
            return new ValueTask<int>(Task.FromException<int>(ex));
        }
    }

    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(Read(buffer, offset, count));
        }
        catch (Exception ex)
        {
            return Task.FromException<int>(ex);
        }
    }

    public Task<int> ReadAsync(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None);

    public int ReadByte()
    {
        var buffer = new byte[1];
        return Read(buffer, 0, 1) == 1 ? buffer[0] : -1;
    }

    public void Write(ReadOnlySpan<byte> buffer)
    {
        var array = buffer.ToArray();
        Write(array, 0, array.Length);
    }

    public Task WriteAsync(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None);

    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return new ValueTask(Task.FromException(ex));
        }
    }

    public void WriteByte(byte value) => Write(new[] { value }, 0, 1);

    public Stream ToStream()
    {
        throw new NotImplementedException("ToStream not implemented for EmailDBFileStream");
    }
}