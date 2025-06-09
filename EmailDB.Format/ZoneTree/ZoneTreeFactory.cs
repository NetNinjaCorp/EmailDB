using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tenray.ZoneTree.Logger;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree;
using Tenray.ZoneTree.Serializers;
using Tenray.ZoneTree.Comparers;
using Tenray.ZoneTree.PresetTypes;
using EmailDB.Format.FileManagement;
using Tenray.ZoneTree.WAL;

namespace EmailDB.Format.ZoneTree;

/// <summary>
/// Factory for creating and managing ZoneTree instances integrated with EmailDB storage
/// </summary>
public class EmailDBZoneTreeFactory<TKey, TValue> : IDisposable
{
    private readonly RawBlockManager _blockManager;
    private readonly bool _enableCompression;
    private readonly CompressionMethod _compressionMethod;
    private readonly Tenray.ZoneTree.Logger.ILogger _logger;
    private readonly string _dataDirectory;
    private ZoneTreeFactory<TKey, TValue> Factory { get; set; }

    public EmailDBZoneTreeFactory(RawBlockManager blockManager,
        bool enableCompression = true,
        CompressionMethod compressionMethod = CompressionMethod.LZ4,
        Tenray.ZoneTree.Logger.ILogger logger = null,
        string dataDirectory = null)
    {
        _blockManager = blockManager;
        _enableCompression = enableCompression;
        _compressionMethod = compressionMethod;
        _logger = logger ?? new Tenray.ZoneTree.Logger.ConsoleLogger();
        _dataDirectory = dataDirectory;
    }

    public bool CreateZoneTree(string name)
    {
        Factory = new ZoneTreeFactory<TKey, TValue>();

        // Configure options
        Factory.Configure(options =>
        {
            options.Logger = _logger;
            
            // Note: ZoneTree will use its internal file paths based on segment names
            // The data directory parameter is kept for future use but not used here
            
            // Use the null WAL provider to completely disable WAL
            options.WriteAheadLogProvider = new NullWriteAheadLogProvider();

            // For string types, use our custom StringSerializer which implements both interfaces
            if (typeof(TKey) == typeof(string) && typeof(TValue) == typeof(string))
            {
                var stringSerializer = new StringSerializer();
                options.KeySerializer = stringSerializer as ISerializer<TKey>;
                options.ValueSerializer = stringSerializer as ISerializer<TValue>;
                options.Comparer = stringSerializer as IRefComparer<TKey>;
            }
            else
            {
                // Use built-in serializers and comparers for other types
                var keySerializer = ComponentsForKnownTypes.GetSerializer<TKey>();
                var valueSerializer = ComponentsForKnownTypes.GetSerializer<TValue>();
                var comparer = ComponentsForKnownTypes.GetComparer<TKey>();
                
                // Validate we got the required components
                if (keySerializer == null)
                    throw new InvalidOperationException($"No serializer available for key type {typeof(TKey).Name}");
                if (valueSerializer == null)
                    throw new InvalidOperationException($"No serializer available for value type {typeof(TValue).Name}");
                if (comparer == null)
                    throw new InvalidOperationException($"No comparer available for key type {typeof(TKey).Name}");
                    
                options.KeySerializer = keySerializer;
                options.ValueSerializer = valueSerializer;
                options.Comparer = comparer;
            }
            
            // Set up device manager for segment storage
            options.RandomAccessDeviceManager = new RandomAccessDeviceManager(_blockManager, name);
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
        // Factory disposal handled by ZoneTree
    }
}