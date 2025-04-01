using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.Blocks;
[ProtoContract]
[ProtoInclude(100, typeof(HeaderContent))]
[ProtoInclude(101, typeof(FolderTreeContent))]
[ProtoInclude(102, typeof(FolderContent))]
[ProtoInclude(103, typeof(SegmentContent))]
[ProtoInclude(104, typeof(MetadataContent))]
[ProtoInclude(105, typeof(CleanupContent))]
[ProtoInclude(106, typeof(WALContent))]
public class BlockContent
{
    // Header content


}