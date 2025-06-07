using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.Blocks;
public enum BlockType
{
    Metadata = 1,
    WAL = 2,
    Spacer= 3,
    FolderTree = 4,
    Folder = 5,
    Segment = 6,   
    Cleanup = 7,    
}