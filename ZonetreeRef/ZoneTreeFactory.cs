﻿using Tenray.ZoneTree.Exceptions;
using Tenray.ZoneTree.Segments.Disk;
using Tenray.ZoneTree.Transactional;
using Tenray.ZoneTree.WAL;
using Tenray.ZoneTree.Core;
using Tenray.ZoneTree.AbstractFileStream;
using Tenray.ZoneTree.Comparers;
using Tenray.ZoneTree.Serializers;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Logger;
using Tenray.ZoneTree.Segments.RandomAccess;
using Tenray.ZoneTree.PresetTypes;

namespace Tenray.ZoneTree;

/// <summary>
/// The factory to open or create a ZoneTree.
/// </summary>
/// <typeparam name="TKey">The key type</typeparam>
/// <typeparam name="TValue">The value type</typeparam>
public sealed class ZoneTreeFactory<TKey, TValue>
{
    string WalDirectory;

    readonly IFileStreamProvider FileStreamProvider;

    Func<ZoneTreeOptions<TKey, TValue>, IWriteAheadLogProvider> GetWriteAheadLogProvider;

    Func<ZoneTreeOptions<TKey, TValue>, ITransactionLog<TKey, TValue>> GetTransactionLog
        = (options) => new BasicTransactionLog<TKey, TValue>(options);

    ITransactionLog<TKey, TValue> TransactionLog;

    /// <summary>
    /// The options to be configured by this factory.
    /// </summary>
    public ZoneTreeOptions<TKey, TValue> Options { get; private set; } = new();

    /// <summary>
    /// Creates a new factory.
    /// </summary>
    /// <param name="fileStreamProvider">The FileStreamProvider. When it is not given, the LocalFileStreamProvider is used.</param>
    public ZoneTreeFactory(IFileStreamProvider fileStreamProvider = null)
    {
        if (fileStreamProvider == null)
            fileStreamProvider = new LocalFileStreamProvider();
        FileStreamProvider = fileStreamProvider;

        GetWriteAheadLogProvider = (options) =>
            WalDirectory == null ?
            new WriteAheadLogProvider(options.Logger, fileStreamProvider) :
            new WriteAheadLogProvider(options.Logger, fileStreamProvider, WalDirectory);
    }

    /// <summary>
    /// Assigns a logger for ZoneTree.
    /// </summary>
    /// <param name="logger"></param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue> SetLogger(ILogger logger)
    {
        Options.Logger = logger;
        return this;
    }

    /// <summary>
    /// Sets log level of current ZoneTree logger.
    /// </summary>
    /// <param name="logLevel"></param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue> SetLogLevel(LogLevel logLevel)
    {
        Options.Logger.LogLevel = logLevel;
        return this;
    }

    /// <summary>
    /// Sets the key-comparer.
    /// </summary>
    /// <param name="comparer">The key-comparer.</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue> SetComparer(IRefComparer<TKey> comparer)
    {
        Options.Comparer = comparer;
        return this;
    }

    /// <summary>
    /// Configures mutable segment maximum item count.
    /// </summary>
    /// <param name="mutableSegmentMaxItemCount">The maximum item count</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetMutableSegmentMaxItemCount(int mutableSegmentMaxItemCount)
    {
        Options.MutableSegmentMaxItemCount = mutableSegmentMaxItemCount;
        return this;
    }

    /// <summary>
    /// Configures disk segment maximum item count.
    /// </summary>
    /// <param name="diskSegmentMaxItemCount">The maximum item count</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetDiskSegmentMaxItemCount(int diskSegmentMaxItemCount)
    {
        Options.DiskSegmentMaxItemCount = diskSegmentMaxItemCount;
        return this;
    }

    /// <summary>
    /// Sets the data directory. Default is "./data"
    /// </summary>
    /// <param name="dataDirectory">The data directory</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetDataDirectory(string dataDirectory)
    {
        Options.RandomAccessDeviceManager = new RandomAccessDeviceManager(
            Options.Logger,
            FileStreamProvider, dataDirectory);
        WalDirectory = dataDirectory;
        return this;
    }

    /// <summary>
    /// Configures the disk segment compression block size.
    /// </summary>
    /// <param name="blockSize">The block size</param>
    /// <returns>ZoneTree Factory</returns>
    /// <exception cref="Exception">Thrown when compression block size is not valid.</exception>
    public ZoneTreeFactory<TKey, TValue>
        SetDiskSegmentCompressionBlockSize(int blockSize)
    {
        if (blockSize < 8 * 1024)
            throw new Exception("Compression Block size cannot be smaller than 8KB");
        if (blockSize > 1024 * 1024 * 1024)
            throw new Exception("Compression Block size cannot be greater than 1GB");
        Options.DiskSegmentOptions.CompressionBlockSize = blockSize;
        return this;
    }

    /// <summary>
    /// Sets configuration options.
    /// </summary>
    /// <param name="options">The configuration options.</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue> SetOptions(ZoneTreeOptions<TKey, TValue> options)
    {
        Options = options;
        return this;
    }

    /// <summary>
    /// Sets random access device manager.
    /// </summary>
    /// <param name="randomAccessDeviceManager">The random access device manager.</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetRandomAccessDeviceManager(IRandomAccessDeviceManager randomAccessDeviceManager)
    {
        Options.RandomAccessDeviceManager = randomAccessDeviceManager;
        return this;
    }

    /// <summary>
    /// Sets the key serializer.
    /// </summary>
    /// <param name="keySerializer">The key serializer</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetKeySerializer(ISerializer<TKey> keySerializer)
    {
        if (Options.WriteAheadLogProvider != null)
            throw new CannotSetSerializersAfterWriteAheadLogProviderInitializedException();

        if (TransactionLog != null)
            throw new CannotSetSerializersAfterTransactionLogInitializedException();

        Options.KeySerializer = keySerializer;
        return this;
    }

    /// <summary>
    /// Sets the value serializer.
    /// </summary>
    /// <param name="valueSerializer">The value serializer</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetValueSerializer(ISerializer<TValue> valueSerializer)
    {
        if (Options.WriteAheadLogProvider != null)
            throw new CannotSetSerializersAfterWriteAheadLogProviderInitializedException();

        if (TransactionLog != null)
            throw new CannotSetSerializersAfterTransactionLogInitializedException();

        Options.ValueSerializer = valueSerializer;
        return this;
    }

    /// <summary>
    /// Sets write ahead log directory.
    /// </summary>
    /// <param name="walDirectory">The write ahead log directory.</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetWriteAheadLogDirectory(string walDirectory)
    {
        WalDirectory = walDirectory;
        return this;
    }

    void InitWriteAheadLogProvider()
    {
        if (Options.WriteAheadLogProvider != null)
            return;
        FillMissingOptionsForKnownTypes();
        Options.WriteAheadLogProvider = GetWriteAheadLogProvider(Options);
    }

    void InitTransactionLog()
    {
        if (TransactionLog != null)
            return;
        FillMissingOptionsForKnownTypes();
        TransactionLog = GetTransactionLog(Options);
    }

    /// <summary>
    /// Sets write ahead log provider getter.
    /// </summary>
    /// <param name="walProviderGetter">The write ahead log provider getter.</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetWriteAheadLogProvider(Func<ZoneTreeOptions<TKey, TValue>, IWriteAheadLogProvider> walProviderGetter)
    {
        GetWriteAheadLogProvider = walProviderGetter;
        return this;
    }

    /// <summary>
    /// Configures ZoneTree options.
    /// </summary>
    /// <param name="configure">The options configurator delegate</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        Configure(Action<ZoneTreeOptions<TKey, TValue>> configure)
    {
        configure(Options);
        return this;
    }

    /// <summary>
    /// Configures the write ahead log options.
    /// </summary>
    /// <param name="configure">The write ahead log options configurator delegate</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        ConfigureWriteAheadLogOptions(Action<WriteAheadLogOptions> configure)
    {
        configure(Options.WriteAheadLogOptions);
        return this;
    }

    /// <summary>
    /// Configures the disk segment options.
    /// </summary>
    /// <param name="configure">The disk segment options configurator delegate</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        ConfigureDiskSegmentOptions(Action<DiskSegmentOptions> configure)
    {
        configure(Options.DiskSegmentOptions);
        return this;
    }
    /// <summary>
    /// Configures the transaction log.
    /// </summary>
    /// <param name="configure">The transaction log configurator delegate</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        ConfigureTransactionLog(Action<ITransactionLog<TKey, TValue>> configure)
    {
        InitWriteAheadLogProvider();
        InitTransactionLog();
        configure(TransactionLog);
        return this;
    }

    /// <summary>
    /// Assigns value deletion marker delegate.
    /// </summary>
    /// <param name="markValueDeleted">The value deletion marker delegate</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetMarkValueDeletedDelegate(MarkValueDeletedDelegate<TValue> markValueDeleted)
    {
        Options.MarkValueDeleted = markValueDeleted;
        return this;
    }

    /// <summary>
    /// Assigns key-value pair deletion query delegate.
    /// </summary>
    /// <param name="isDeleted">The key-value pair deleted query delagate.</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetIsDeletedDelegate(IsDeletedDelegate<TKey, TValue> isDeleted)
    {
        Options.IsDeleted = isDeleted;
        return this;
    }

    /// <summary>
    /// Sets the transaction log creator delegate.
    /// </summary>
    /// <param name="transactionLogGetter">The transaction log creator delegate</param>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue>
        SetTransactionLog(Func<ZoneTreeOptions<TKey, TValue>, ITransactionLog<TKey, TValue>> transactionLogGetter)
    {
        GetTransactionLog = transactionLogGetter;
        return this;
    }

    void FillMissingOptionsForKnownTypes()
    {
        if (Options.RandomAccessDeviceManager == null)
        {
            Options.RandomAccessDeviceManager = new RandomAccessDeviceManager(
                Options.Logger,
                FileStreamProvider);
        }
        Options.CreateDefaultDeleteDelegates();
        FillComparer();
        FillKeySerializer();
        FillValueSerializer();
    }

    void FillComparer()
    {
        if (Options.Comparer != null)
            return;
        Options.Comparer = ComponentsForKnownTypes.GetComparer<TKey>();
    }

    void FillKeySerializer()
    {
        if (Options.KeySerializer != null)
            return;
        Options.KeySerializer = ComponentsForKnownTypes.GetSerializer<TKey>();
    }

    void FillValueSerializer()
    {
        if (Options.ValueSerializer != null)
            return;
        Options.ValueSerializer = ComponentsForKnownTypes.GetSerializer<TValue>();
    }

    /// <summary>
    /// Opens or creates a ZoneTree.
    /// </summary>
    /// <returns>ZoneTree Factory</returns>
    public IZoneTree<TKey, TValue> OpenOrCreate()
    {
        FillMissingOptionsForKnownTypes();
        InitWriteAheadLogProvider();
        var loader = new ZoneTreeLoader<TKey, TValue>(Options);
        if (loader.ZoneTreeMetaExists)
        {
            var zoneTree = loader.LoadZoneTree();
            return zoneTree;
        }
        return new ZoneTree<TKey, TValue>(Options);
    }

    /// <summary>
    /// Creates a ZoneTree.
    /// </summary>
    /// <returns>ZoneTree Factory</returns>
    /// <exception cref="DatabaseAlreadyExistsException">Thrown when the database exists in the location.</exception>
    public IZoneTree<TKey, TValue> Create()
    {
        FillMissingOptionsForKnownTypes();
        InitWriteAheadLogProvider();
        var loader = new ZoneTreeLoader<TKey, TValue>(Options);
        if (loader.ZoneTreeMetaExists)
            throw new DatabaseAlreadyExistsException();
        return new ZoneTree<TKey, TValue>(Options);
    }

    /// <summary>
    /// Opens a ZoneTree.
    /// </summary>
    /// <returns>ZoneTree Factory</returns>
    /// <exception cref="DatabaseNotFoundException">Thrown when database is not found in the location.</exception>
    public IZoneTree<TKey, TValue> Open()
    {
        FillMissingOptionsForKnownTypes();
        InitWriteAheadLogProvider();
        var loader = new ZoneTreeLoader<TKey, TValue>(Options);
        if (!loader.ZoneTreeMetaExists)
            throw new DatabaseNotFoundException();
        var zoneTree = loader.LoadZoneTree();
        return zoneTree;
    }

    /// <summary>
    /// Opens or creates a transactional ZoneTree.
    /// </summary>
    /// <returns>ZoneTree Factory</returns>
    public ITransactionalZoneTree<TKey, TValue> OpenOrCreateTransactional()
    {
        var zoneTree = OpenOrCreate();
        InitTransactionLog();
        return new OptimisticZoneTree<TKey, TValue>(Options, TransactionLog, zoneTree);
    }

    /// <summary>
    /// Creates a transactional ZoneTree.
    /// </summary>
    /// <returns>ZoneTree Factory</returns>
    /// <exception cref="DatabaseAlreadyExistsException">Thrown when the database exists in the location.</exception>
    public ITransactionalZoneTree<TKey, TValue> CreateTransactional()
    {
        var zoneTree = Create();
        InitTransactionLog();
        return new OptimisticZoneTree<TKey, TValue>(Options, TransactionLog, zoneTree);
    }

    /// <summary>
    /// Opens a transactional ZoneTree.
    /// </summary>
    /// <returns>ZoneTree Factory</returns>
    /// <exception cref="DatabaseNotFoundException">Thrown when database is not found in the location.</exception>
    public ITransactionalZoneTree<TKey, TValue> OpenTransactional()
    {
        var zoneTree = Open();
        InitTransactionLog();
        return new OptimisticZoneTree<TKey, TValue>(Options, TransactionLog, zoneTree);
    }

    /// <summary>
    /// Disables deletion to be able to insert default values of the value type.
    /// Databases created with this option are not able to delete records.
    /// </summary>
    /// <returns>ZoneTree Factory</returns>
    public ZoneTreeFactory<TKey, TValue> DisableDeletion()
    {
        Options.DisableDeletion();
        return this;
    }
}