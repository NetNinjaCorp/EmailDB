using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf.Models;

[ProtoContract]
class SpacerBlock
{
    [ProtoMember(50)]
    public byte[] SpacerData { get; set; }
}
