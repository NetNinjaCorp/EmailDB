using Tenray.ZoneTree.AbstractFileStream;
using Tenray.ZoneTree.Core;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Segments.RandomAccess;

namespace EmailDB.Format.ZoneTree;

public class RandomAccessDeviceManager : IRandomAccessDeviceManager
{
    private readonly StorageManager storageManager;
    private readonly string name;
    private readonly Dictionary<string, IRandomAccessDevice> devices;

    public RandomAccessDeviceManager(StorageManager storageManager, string name)
    {
        this.storageManager = storageManager;
        this.name = name;
        this.devices = new Dictionary<string, IRandomAccessDevice>();
    }

    public int DeviceCount => devices.Count;
    public int ReadOnlyDeviceCount => devices.Values.Count(d => !d.Writable);
    public int WritableDeviceCount => devices.Values.Count(d => d.Writable);
    public IFileStreamProvider FileStreamProvider => new EmailDBFileStreamProvider(storageManager);

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

        if (devices.TryGetValue(key, out var existing))
        {
            if (deleteIfExists)
            {
                if (backupIfDelete)
                {
                    // TODO: Implement backup
                }
                existing.Delete();
                devices.Remove(key);
            }
            else
            {
                return existing;
            }
        }

        var device = new RandomAccessDevice(storageManager, name, segmentId, category, true);
        devices[key] = device;
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

        if (devices.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var device = new RandomAccessDevice(storageManager, name, segmentId, category, false);
        devices[key] = device;
        return device;
    }

    public void DeleteDevice(long segmentId, string category, bool isCompressed)
    {
        var key = GetDeviceKey(segmentId, category, isCompressed);
        if (devices.TryGetValue(key, out var device))
        {
            device.Delete();
            devices.Remove(key);
        }
    }

    public bool DeviceExists(long segmentId, string category, bool isCompressed)
    {
        var key = GetDeviceKey(segmentId, category, isCompressed);
        return devices.ContainsKey(key);
    }

    public IReadOnlyList<IRandomAccessDevice> GetDevices()
    {
        return devices.Values.ToList();
    }

    public IReadOnlyList<IRandomAccessDevice> GetReadOnlyDevices()
    {
        return devices.Values.Where(d => !d.Writable).ToList();
    }

    public IReadOnlyList<IRandomAccessDevice> GetWritableDevices()
    {
        return devices.Values.Where(d => d.Writable).ToList();
    }

    public void RemoveReadOnlyDevice(long segmentId, string category)
    {
        var key = GetDeviceKey(segmentId, category, false);
        if (devices.TryGetValue(key, out var device) && !device.Writable)
        {
            device.Close();
            devices.Remove(key);
        }
    }

    public void RemoveWritableDevice(long segmentId, string category)
    {
        var key = GetDeviceKey(segmentId, category, false);
        if (devices.TryGetValue(key, out var device) && device.Writable)
        {
            device.Close();
            devices.Remove(key);
        }
    }

    public void CloseAllDevices()
    {
        foreach (var device in devices.Values)
        {
            device.Close();
        }
        devices.Clear();
    }

    public void DropStore()
    {
        foreach (var device in devices.Values)
        {
            device.Delete();
        }
        devices.Clear();
    }

    public string GetFilePath(long segmentId, string category)
    {
        return $"{name}_{category}_{segmentId}";
    }

    private string GetDeviceKey(long segmentId, string category, bool isCompressed)
    {
        return $"{segmentId}_{category}_{isCompressed}";
    }
}