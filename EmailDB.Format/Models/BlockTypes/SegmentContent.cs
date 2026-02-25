using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.BlockTypes;
public class SegmentContent
{
    public long SegmentId { get; set; }
    public byte[] SegmentData { get; set; }
    public string FileName { get; set; }  // Name of the physical file containing this segment
    public long FileOffset { get; set; }  // Offset within the file where the segment data begins
    public int ContentLength { get; set; }  // Length of the email content in bytes
    public long SegmentTimestamp { get; set; }  // When this segment version was created
    public bool IsDeleted { get; set; }  // Soft deletion flag
    public uint Version { get; set; }  // Version number for this segment
    public Dictionary<string, string> Metadata { get; set; } = new();  // Optional metadata for the segment

    // Computed property to help with segment file organization
    public long SegmentFileGroup => SegmentId / 1000;
}
