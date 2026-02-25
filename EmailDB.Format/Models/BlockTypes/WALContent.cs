using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.BlockTypes;
public class WALContent
{
    public Dictionary<string, List<WALEntry>> Entries { get; set; } = new();
    public long NextWALOffset { get; set; } = -1;
    public Dictionary<string, long> CategoryOffsets { get; set; } = new();
}

public class WALEntry
{
    public byte[] SerializedKey { get; set; }
    public byte[] SerializedValue { get; set; }
    public long OpIndex { get; set; }
    public string Category { get; set; }
    public long SegmentId { get; set; }
}