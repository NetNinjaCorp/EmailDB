using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format;
public interface iBlockContentSerializer
{
    public byte[] Serialize<T>(T obj);

    public T Deserialize<T>(byte[] payload);
}
