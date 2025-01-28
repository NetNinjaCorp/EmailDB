using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tenray.ZoneTree.Logger;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree;

namespace EmailDB.Format.ZoneTree;
/// <summary>
/// Factory for creating and managing ZoneTree instances integrated with EmailDB storage
/// </summary>
public class EmailDBZoneTreeFactory<TKey, TValue>
{
    private readonly StorageManager _storageManager;
    private readonly bool _enableCompression;
    private readonly CompressionMethod _compressionMethod;
    private readonly ILogger _logger;

    public EmailDBZoneTreeFactory(StorageManager storageManager,
        bool enableCompression = true,
        CompressionMethod compressionMethod = CompressionMethod.LZ4,
        ILogger logger = null)
    {
        _storageManager = storageManager;
        _enableCompression = enableCompression;
        _compressionMethod = compressionMethod;
        _logger = logger ?? new ConsoleLogger();
    }

    public IZoneTree<TKey, TValue> CreateZoneTree(string name)
    {
        var factory = new ZoneTreeFactory<TKey, TValue>();

        // Set up WAL configuration
        var walOptions = new WriteAheadLogOptions
        {
            CompressionMethod = _compressionMethod,
            WriteAheadLogMode = _enableCompression ?
                WriteAheadLogMode.SyncCompressed :
                WriteAheadLogMode.Sync
        };

        // Configure options
        factory.Configure(options =>
        {
            options.Logger = _logger;
            options.WriteAheadLogOptions = walOptions;
           
            //options.DiskSegmentOptions = new DiskSegmentOptions
            //{
            //    CompressionMethod = _compressionMethod,
            //    // Customize as needed
            //    MaximumRecordCount = 2000000,
            //    MinimumRecordCount = 100,
            //    CompressionBlockSize = 64 * 1024, // 64KB blocks
            //    CompressionLevel = 2 // Fast compression
            //};

            // Set WAL provider to use EmailDB storage
            options.WriteAheadLogProvider = new WriteAheadLogProvider(_storageManager, name);

            // Set up device manager for segment storage
            options.RandomAccessDeviceManager = new RandomAccessDeviceManager(_storageManager, name);
        });

        return factory.OpenOrCreate();
    }
}