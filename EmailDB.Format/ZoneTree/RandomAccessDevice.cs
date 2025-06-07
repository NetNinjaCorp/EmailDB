//using EmailDB.Format.Models.BlockTypes;
//using Tenray.ZoneTree.Segments.Block;
//using Tenray.ZoneTree.Segments.RandomAccess;

//namespace EmailDB.Format.ZoneTree;

//public sealed class RandomAccessDevice : IRandomAccessDevice
//{
//    private readonly SegmentManager segmentManager;
//    private readonly long segmentId;
//    private readonly string category;
//    private long currentPosition;
//    private int deviceLength;
//    private volatile bool isDisposed;
//    private volatile bool isSealed;
//    private SegmentContent currentSegment;

//    public bool Writable { get; private set; }
//    public long Length => deviceLength;
//    public int ReadBufferCount => 0;
//    public string FilePath { get; }
//    public long SegmentId => segmentId;

//    public RandomAccessDevice(
//        SegmentManager segmentManager,       
//        long segmentId,
//        string category,
//        bool writable)
//    {
//        this.segmentManager = segmentManager;
        
//        this.segmentId = segmentId;
//        this.category = category;
//        Writable = writable;
//        FilePath = $"{segmentId}_{category}";

//        if (!writable)
//        {
//            // For read-only device, load existing segment content
//            if(segmentManager.TryGetSegment(FilePath, out  SegmentContent segment))
//            {
//                currentSegment = segment;
//                deviceLength = segment.ContentLength;            }
            
//            if (segment != null)
//            {
//                currentSegment = segment;
//                deviceLength = segment.ContentLength;
//            }
//        }
//        else
//        {
//            // For writable device, create new segment
//            currentSegment = new SegmentContent
//            {
//                SegmentId = segmentId,
//                SegmentData = new byte[0],
//                ContentLength = 0,
//                SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
//                Version = 1
//            };
//        }
//    }

//    public long AppendBytesReturnPosition(Memory<byte> bytes)
//    {
//        if (!Writable || isSealed || isDisposed)
//            throw new InvalidOperationException("Device is not writable, sealed or disposed");

//        var position = currentPosition;

//        // Create new byte array with appended data
//        var newData = new byte[currentSegment.SegmentData.Length + bytes.Length];
//        if (currentSegment.SegmentData.Length > 0)
//            currentSegment.SegmentData.CopyTo(newData, 0);
//        bytes.CopyTo(newData.AsMemory(currentSegment.SegmentData.Length));

//        // Update segment
//        currentSegment.SegmentData = newData;
//        currentSegment.ContentLength += bytes.Length;
//        deviceLength = currentSegment.ContentLength;
//        currentPosition += bytes.Length;

//        return position;
//    }

//    public Memory<byte> GetBytes(long offset, int length, SingleBlockPin blockPin = null)
//    {
//        if (offset >= deviceLength)
//            return Memory<byte>.Empty;

//        var available = deviceLength - offset;
//        var bytesToRead = Math.Min(length, available);

//        return new Memory<byte>(currentSegment.SegmentData, (int)offset, (int)bytesToRead);
//    }

//    public void ClearContent()
//    {
//        if (!Writable || isSealed || isDisposed)
//            throw new InvalidOperationException("Device is not writable, sealed or disposed");

//        currentSegment.SegmentData = new byte[0];
//        currentSegment.ContentLength = 0;
//        deviceLength = 0;
//        currentPosition = 0;
//    }

//    public void Close()
//    {
//        if (!isDisposed)
//        {
//            if (Writable && !isSealed)
//            {
//                // Write final segment data before closing
//                WriteSegment();
//            }
//            isDisposed = true;
//        }
//    }

//    private void WriteSegment()
//    {
//        segmentManager.WriteSegment(currentSegment);     
//    }

//    public void Delete()
//    {
//        if (Writable)
//        {
//            segmentManager.DeleteSegment(FilePath);
//        }
//        isDisposed = true;
//    }

//    public void Dispose()
//    {
//        Close();
//    }

//    public void SealDevice()
//    {
//        if (Writable && !isSealed)
//        {
//            WriteSegment();
//            isSealed = true;
//            Writable = false;
//        }
//    }

//    public int ReleaseInactiveCachedBuffers(long ticks)
//    {
//        // No caching implemented
//        return 0;
//    }
//}