using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models;

public enum BlockType : byte
{
    Metadata = 0,
    WAL = 1,
    FolderTree = 2,
    Folder = 3,
    Segment = 4,
    Cleanup = 5,
    ZoneTreeSegment_KV = 6,      // ZoneTree Key-Value segment
    ZoneTreeSegment_Vector = 7,  // ZoneTree Vector index segment  
    FreeSpace = 8                // Marked as free for reuse
}


