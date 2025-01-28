using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models;
public enum BlockType
{
    Header = 1,
    FolderTree = 2,
    Folder = 3,
    Segment = 4,
    Metadata = 5,
    Cleanup = 6,
    WAL = 7
}