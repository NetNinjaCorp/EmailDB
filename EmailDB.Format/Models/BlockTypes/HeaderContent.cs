using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.BlockTypes;
public class HeaderContent
{
    public int FileVersion { get; set; }
    public long FirstMetadataOffset { get; set; }
    public long FirstFolderTreeOffset { get; set; }
    public long FirstCleanupOffset { get; set; }
}
