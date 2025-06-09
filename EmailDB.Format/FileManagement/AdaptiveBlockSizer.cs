using System;

namespace EmailDB.Format.FileManagement;

public class AdaptiveBlockSizer
{
    private readonly (long dbSize, int blockSize)[] _sizeProgression = new[]
    {
        (5L * 1024 * 1024 * 1024,     50 * 1024 * 1024),   // < 5GB: 50MB blocks
        (25L * 1024 * 1024 * 1024,   100 * 1024 * 1024),   // < 25GB: 100MB blocks
        (100L * 1024 * 1024 * 1024,  250 * 1024 * 1024),   // < 100GB: 250MB blocks
        (500L * 1024 * 1024 * 1024,  500 * 1024 * 1024),   // < 500GB: 500MB blocks
        (long.MaxValue,             1024 * 1024 * 1024)     // >= 500GB: 1GB blocks
    };
    
    public int GetTargetBlockSize(long currentDatabaseSize)
    {
        foreach (var (threshold, size) in _sizeProgression)
        {
            if (currentDatabaseSize < threshold)
                return size;
        }
        return _sizeProgression[^1].blockSize;
    }
    
    public int GetTargetBlockSizeMB(long currentDatabaseSize)
    {
        return GetTargetBlockSize(currentDatabaseSize) / (1024 * 1024);
    }
}