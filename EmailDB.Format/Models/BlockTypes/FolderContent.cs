using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.BlockTypes;
public class FolderContent
{
    public long FolderId { get; set; }

    public long ParentFolderId { get; set; }

    public string Name { get; set; }

    public List<EmailHashedID> EmailIds { get; set; } = new();
}