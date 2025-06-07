using Tenray.ZoneTree.AbstractFileStream;
using Tenray.ZoneTree.Core;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Segments.RandomAccess;
using EmailDB.Format.FileManagement;

namespace EmailDB.Format.ZoneTree;

public class RandomAccessDeviceManager : IRandomAccessDeviceManager
{
    private readonly RawBlockManager _blockManager;
    private readonly string _name;
    private readonly Dictionary<string, IRandomAccessDevice> _devices;

    public RandomAccessDeviceManager(RawBlockManager blockManager, string name)
    {
        _blockManager = blockManager;
        _name = name;
        _devices = new Dictionary<string, IRandomAccessDevice>();
    }

    public int DeviceCount => _devices.Count;
    public int ReadOnlyDeviceCount => _devices.Values.Count(d => !d.Writable);
    public int WritableDeviceCount => _devices.Values.Count(d => d.Writable);
    public IFileStreamProvider FileStreamProvider => new EmailDBFileStreamProvider(_blockManager);

    public IRandomAccessDevice CreateWritableDevice(
        long segmentId,
        string category,
        bool isCompressed,
        int compressionBlockSize,
        bool deleteIfExists,
        bool backupIfDelete,
        CompressionMethod compressionMethod,
        int compressionLevel)
    {
        var key = GetDeviceKey(segmentId, category, isCompressed);

        if (_devices.TryGetValue(key, out var existing))
        {
            if (deleteIfExists)
            {
                if (backupIfDelete)
                {
                    // TODO: Implement backup functionality
                }
                existing.Delete();
                _devices.Remove(key);
            }
            else
            {
                return existing;
            }
        }

        Console.WriteLine($"🏭 RandomAccessDeviceManager.CreateWritableDevice: segmentId={segmentId}, category='{category}'");
        var device = new RandomAccessDevice(_blockManager, segmentId, category, true);
        _devices[key] = device;
        return device;
    }

    public IRandomAccessDevice GetReadOnlyDevice(
        long segmentId,
        string category,
        bool isCompressed,
        int compressionBlockSize,
        CompressionMethod compressionMethod,
        int compressionLevel)
    {
        var key = GetDeviceKey(segmentId, category, isCompressed);

        if (_devices.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var device = new RandomAccessDevice(_blockManager, segmentId, category, false);
        _devices[key] = device;
        return device;
    }

    public void DeleteDevice(long segmentId, string category, bool isCompressed)
    {
        var key = GetDeviceKey(segmentId, category, isCompressed);
        if (_devices.TryGetValue(key, out var device))
        {
            device.Delete();
            _devices.Remove(key);
        }
    }

    public bool DeviceExists(long segmentId, string category, bool isCompressed)
    {
        var key = GetDeviceKey(segmentId, category, isCompressed);
        return _devices.ContainsKey(key);
    }

    public IReadOnlyList<IRandomAccessDevice> GetDevices()
    {
        return _devices.Values.ToList();
    }

    public IReadOnlyList<IRandomAccessDevice> GetReadOnlyDevices()
    {
        return _devices.Values.Where(d => !d.Writable).ToList();
    }

    public IReadOnlyList<IRandomAccessDevice> GetWritableDevices()
    {
        return _devices.Values.Where(d => d.Writable).ToList();
    }

    public void RemoveReadOnlyDevice(long segmentId, string category)
    {
        var key = GetDeviceKey(segmentId, category, false);
        if (_devices.TryGetValue(key, out var device) && !device.Writable)
        {
            device.Close();
            _devices.Remove(key);
        }
    }

    public void RemoveWritableDevice(long segmentId, string category)
    {
        var key = GetDeviceKey(segmentId, category, false);
        if (_devices.TryGetValue(key, out var device) && device.Writable)
        {
            device.Close();
            _devices.Remove(key);
        }
    }

    public void CloseAllDevices()
    {
        foreach (var device in _devices.Values)
        {
            device.Close();
        }
        _devices.Clear();
    }

    public void DropStore()
    {
        foreach (var device in _devices.Values)
        {
            device.Delete();
        }
        _devices.Clear();
    }

    public string GetFilePath(long segmentId, string category)
    {
        return $"{_name}_{category}_{segmentId}";
    }

    private string GetDeviceKey(long segmentId, string category, bool isCompressed)
    {
        return $"{segmentId}_{category}_{isCompressed}";
    }
}