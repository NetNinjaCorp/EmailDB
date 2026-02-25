using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf.Models;

[ProtoContract]
public enum BlockType
{
    Unknown = 0,
    Metadata = 1,
    WAL = 2,
    Spacer = 3,
    FolderTree = 4,
    Folder = 5,
    Segment = 6,
    Cleanup = 7,
}