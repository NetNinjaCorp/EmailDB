﻿using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Tenray.ZoneTree.Segments.Block;
using Tenray.ZoneTree.Segments.RandomAccess;

namespace EmailDB.Format.ZoneTree;

public sealed class RandomAccessDevice : IRandomAccessDevice
{
    private readonly RawBlockManager _blockManager;
    private readonly long _segmentId;
    private readonly string _category;
    private readonly int _blockId;
    private long _currentPosition;
    private int _deviceLength;
    private volatile bool _isDisposed;
    private volatile bool _isSealed;
    private byte[] _data;
    private bool _isDirty;

    public bool Writable { get; private set; }
    public long Length => _deviceLength;
    public int ReadBufferCount => 0;
    public string FilePath { get; }
    public long SegmentId => _segmentId;

    public RandomAccessDevice(
        RawBlockManager blockManager,       
        long segmentId,
        string category,
        bool writable)
    {
        _blockManager = blockManager;
        _segmentId = segmentId;
        _category = category;
        Writable = writable;
        FilePath = $"{segmentId}_{category}";
        _blockId = FilePath.GetHashCode(); // Use consistent hash for block ID

        // Always try to load existing data first
        LoadExistingData();
        
        // For writable devices, set position to end of existing data
        if (writable && _data.Length > 0)
        {
            _currentPosition = _data.Length;
        }
    }

    private void LoadExistingData()
    {
        ZoneTreeLogger.LogSegmentOperation("LoadExistingData", _segmentId, _category, 0, $"BlockId: {_blockId}");
        var readResult = _blockManager.ReadBlockAsync(_blockId).Result;
        if (readResult.IsSuccess)
        {
            _data = readResult.Value.Payload;
            _deviceLength = _data.Length;
            ZoneTreeLogger.LogSegmentOperation("LoadedExisting", _segmentId, _category, _deviceLength, $"BlockId: {_blockId}");
            ZoneTreeLogger.LogData($"Segment {_segmentId}/{_category}", _data);
        }
        else
        {
            _data = Array.Empty<byte>();
            _deviceLength = 0;
            ZoneTreeLogger.LogSegmentOperation("NoExistingData", _segmentId, _category, 0, $"BlockId: {_blockId}");
        }
    }

    public long AppendBytesReturnPosition(Memory<byte> bytes)
    {
        if (!Writable || _isSealed || _isDisposed)
            throw new InvalidOperationException("Device is not writable, sealed or disposed");

        var position = _currentPosition;

        // DEBUG: Log that we're being called
        ZoneTreeLogger.LogSegmentOperation("AppendBytes", _segmentId, _category, bytes.Length, $"Position: {position}");

        // Extend data array
        var newData = new byte[_data.Length + bytes.Length];
        if (_data.Length > 0)
            _data.CopyTo(newData, 0);
        bytes.CopyTo(newData.AsMemory(_data.Length));

        _data = newData;
        _deviceLength += bytes.Length;
        _currentPosition += bytes.Length;
        _isDirty = true;

        return position;
    }

    public Memory<byte> GetBytes(long offset, int length, SingleBlockPin blockPin = null)
    {
        if (offset >= _deviceLength)
            return Memory<byte>.Empty;

        var available = _deviceLength - offset;
        var bytesToRead = Math.Min(length, available);

        return new Memory<byte>(_data, (int)offset, (int)bytesToRead);
    }

    public void ClearContent()
    {
        if (!Writable || _isSealed || _isDisposed)
            throw new InvalidOperationException("Device is not writable, sealed or disposed");

        _data = Array.Empty<byte>();
        _deviceLength = 0;
        _currentPosition = 0;
        _isDirty = true;
    }

    public void Close()
    {
        if (!_isDisposed)
        {
            if (Writable && !_isSealed && _isDirty)
            {
                // Save data before closing
                SaveData();
            }
            _isDisposed = true;
        }
    }

    private void SaveData()
    {
        ZoneTreeLogger.LogSegmentOperation("SaveData", _segmentId, _category, _data.Length, $"BlockId: {_blockId}");
        ZoneTreeLogger.LogData($"Saving segment {_segmentId}/{_category}", _data);
        
        var block = new Block
        {
            Version = 1,
            Type = BlockType.ZoneTreeSegment_KV,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = _blockId,
            Payload = _data
        };

        var result = _blockManager.WriteBlockAsync(block).Result;
        if (result.IsSuccess)
        {
            ZoneTreeLogger.LogSegmentOperation("SaveSuccess", _segmentId, _category, _data.Length, $"BlockId: {_blockId}");
            _isDirty = false;
        }
        else
        {
            ZoneTreeLogger.LogSegmentOperation("SaveFailed", _segmentId, _category, _data.Length, $"BlockId: {_blockId}");
        }
    }

    public void Delete()
    {
        if (Writable)
        {
            // Mark as deleted - in a full implementation we'd have proper deletion
            _data = Array.Empty<byte>();
            _deviceLength = 0;
            _isDirty = true;
        }
        _isDisposed = true;
    }

    public void Dispose()
    {
        Close();
    }

    public void SealDevice()
    {
        if (Writable && !_isSealed)
        {
            SaveData();
            _isSealed = true;
            Writable = false;
        }
    }

    public int ReleaseInactiveCachedBuffers(long ticks)
    {
        // No caching implemented
        return 0;
    }
}