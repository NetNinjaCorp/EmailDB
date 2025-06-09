using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.BlockTypes;
public class FolderTreeContent 
{
    public long RootFolderId { get; set; }
    public Dictionary<string, string> FolderHierarchy { get; set; } = new();
    public Dictionary<string, long> FolderIDs { get; set; } = new();
    public Dictionary<long, long> FolderOffsets { get; set; } = new();

}
