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
    BTreeLeaf = 6,
    BTreeInternal = 7,
    IndexRoot = 8,
    EmailContent = 9,
    KeyStore = 10
}


