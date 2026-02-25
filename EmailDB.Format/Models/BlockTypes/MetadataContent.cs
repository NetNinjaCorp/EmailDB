using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.BlockTypes;

public class MetadataContent
{
    private const int metadataSpace = 10;
    public long WALOffset { get; set; } = -1;
    public long FolderTreeOffset { get; set; } = -1;
    public Dictionary<string, long> SegmentOffsets { get; set; } = new();
    public List<long> OutdatedOffsets { get; set; } = new();

}
