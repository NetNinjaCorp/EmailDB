using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.CapnProto.Models
{
    public enum BlockType : byte
    {
        Metadata = 0,
        WAL = 1,
        FolderTree = 2,
        Folder = 3,
        Segment = 4,
        Cleanup = 5
    }

    public struct BlockLocation
    {
        public long Position { get; set; }
        public long Length { get; set; }
    }
}
