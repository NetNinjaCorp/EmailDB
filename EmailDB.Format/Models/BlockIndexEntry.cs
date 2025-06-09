using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models;
internal class BlockIndexEntry
{
    public long BlockId { get; set; }
    public long Offset { get; set; }
    public BlockType Type { get; set; }
    public object Content { get; set; }
    public DateTime LastAccess { get; set; }
    public string Key { get; set; } // Folder path, segment ID, etc.
}