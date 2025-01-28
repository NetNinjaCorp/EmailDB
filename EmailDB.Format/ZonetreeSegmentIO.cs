using EmailDB.Format.Models;

namespace EmailDB.Format;

// ZoneTree IO handler for segment files
public class ZoneTreeSegmentIO : IDisposable
{
    private readonly string basePath;
    private readonly Dictionary<string, FileStream> segmentStreams = new();
    private readonly object lockObj = new object();

    public ZoneTreeSegmentIO(string basePath)
    {
        this.basePath = basePath;
        Directory.CreateDirectory(basePath);
    }

    public void WriteSegment(SegmentContent segment)
    {
        var fileName = GetSegmentFileName(segment.SegmentId);
        lock (lockObj)
        {
            if (!segmentStreams.TryGetValue(fileName, out var stream))
            {
                stream = new FileStream(Path.Combine(basePath, fileName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                segmentStreams[fileName] = stream;
            }

            segment.FileOffset = stream.Length;
            segment.ContentLength = segment.SegmentData.Length;
            segment.FileName = fileName;

            stream.Position = segment.FileOffset;
            stream.Write(segment.SegmentData);
            stream.Flush(true);
        }
    }

    public byte[] ReadSegment(SegmentContent segment)
    {
        lock (lockObj)
        {
            if (!segmentStreams.TryGetValue(segment.FileName, out var stream))
            {
                stream = new FileStream(Path.Combine(basePath, segment.FileName),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
                segmentStreams[segment.FileName] = stream;
            }

            stream.Position = segment.FileOffset;
            var buffer = new byte[segment.ContentLength];
            stream.Read(buffer, 0, segment.ContentLength);
            return buffer;
        }
    }

    private string GetSegmentFileName(ulong segmentId)
    {
        return $"segment_{segmentId / 1000:D3}.dat";
    }

    public void Dispose()
    {
        foreach (var stream in segmentStreams.Values)
        {
            stream.Dispose();
        }
        segmentStreams.Clear();
    }
}