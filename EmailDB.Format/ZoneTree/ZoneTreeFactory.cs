using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tenray.ZoneTree.Logger;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree;
using Microsoft.Extensions.Options;
using Tenray.ZoneTree.Exceptions;
using Tenray.ZoneTree.Transactional;
using Tenray.ZoneTree.Serializers;
using Tenray.ZoneTree.Comparers;
using ZoneTree.FullTextSearch.Model;
using ZoneTree.FullTextSearch.QueryLanguage;

namespace EmailDB.Format.ZoneTree;
/// <summary>
/// Factory for creating and managing ZoneTree instances integrated with EmailDB storage
/// </summary>
public class EmailDBZoneTreeFactory<TKey, TValue> : IDisposable where TKey : ISerializer<TKey>,IRefComparer<TKey>,new() where TValue : ISerializer<TValue>,new()

{
    private readonly StorageManager _storageManager;
    private readonly bool _enableCompression;
    private readonly CompressionMethod _compressionMethod;
    private readonly ILogger _logger;
    private ZoneTreeFactory<TKey, TValue> Factory { get; set; }

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

    public bool CreateZoneTree(string name)
    {
        Factory = new ZoneTreeFactory<TKey, TValue>();

        // Set up WAL configuration
        var walOptions = new WriteAheadLogOptions
        {
            CompressionMethod = _compressionMethod,
            WriteAheadLogMode = _enableCompression ?
                WriteAheadLogMode.SyncCompressed :
                WriteAheadLogMode.Sync
        };

        // Configure options
        Factory.Configure(options =>
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
            options.KeySerializer = new TKey();
            options.ValueSerializer = new TValue();
            options.Comparer = new TKey();
            // Set WAL provider to use EmailDB storage
            options.WriteAheadLogProvider = new WriteAheadLogProvider(_storageManager, name);
            // Set up device manager for segment storage
            options.RandomAccessDeviceManager = new RandomAccessDeviceManager(_storageManager.segmentManager, name);
        });

        return true;
    }


    public IZoneTree<TKey, TValue> OpenOrCreate()
    {
        return Factory.OpenOrCreate();
    }

    /// <summary>
    /// Assigns key-value pair deletion query delegate.
    /// </summary>
    /// <param name="isDeleted">The key-value pair deleted query delagate.</param>
    /// <returns>ZoneTree Factory</returns>
    public EmailDBZoneTreeFactory<TKey, TValue>
        SetIsDeletedDelegate(IsDeletedDelegate<TKey, TValue> isDeleted)
    {

        Factory.SetIsDeletedDelegate(isDeleted);
        return this;
    }

    /// <summary>
    /// Assigns value deletion marker delegate.
    /// </summary>
    /// <param name="markValueDeleted">The value deletion marker delegate</param>
    /// <returns>ZoneTree Factory</returns>
    public EmailDBZoneTreeFactory<TKey, TValue>
        SetMarkValueDeletedDelegate(MarkValueDeletedDelegate<TValue> markValueDeleted)
    {
        Factory.SetMarkValueDeletedDelegate(markValueDeleted);
        return this;
    }

    /// <summary>
    /// Sets the key serializer.
    /// </summary>
    /// <param name="keySerializer">The key serializer</param>
    /// <returns>ZoneTree Factory</returns>
    public EmailDBZoneTreeFactory<TKey, TValue>
        SetKeySerializer(ISerializer<TKey> keySerializer)
    {
        Factory.SetKeySerializer(keySerializer);
        return this;
    }

    /// <summary>
    /// Sets the key-comparer.
    /// </summary>
    /// <param name="comparer">The key-comparer.</param>
    /// <returns>ZoneTree Factory</returns>
    public EmailDBZoneTreeFactory<TKey, TValue> SetComparer(IRefComparer<TKey> comparer)
    {
        Factory.SetComparer(comparer);
        return this;
    }

    public void Dispose()
    {
        return;
    }
}