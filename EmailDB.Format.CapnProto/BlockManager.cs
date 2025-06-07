using EmailDB.Format.CapnProto.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.CapnProto;

public class BlockManager
{
    private RawBlockManager rawBlockManager;
    private CacheManager cache;
    private Dictionary<long, Block> blockCache = new Dictionary<long, Block>();
    private FolderTreeContent folderTree;


    public BlockManager(string File)
    {
        rawBlockManager = new RawBlockManager(File);
        cache = new CacheManager(rawBlockManager);
        var tmpMeta = cache.GetCachedMetadata().GetAwaiter().GetResult();
        if (tmpMeta is null)
        {
            throw new Exception("Metadata not found");
        }
    }

    public async Task<SegmentContent> GetSegmentAsync(long SegmentId)
    {
        var tmpSegment = await cache.GetSegmentAsync(SegmentId);
        if (tmpSegment is null)
        {
            return null;
        }
        return tmpSegment;

    }

    public async Task<FolderContent> GetFolderAsync(string FolderName)
    {            
        if(folderTree is null)
        {
            folderTree = await GetFolderTreeContentAsync();
        }
        if (folderTree is null)
        {
            return null;
        }
        var FolderId = folderTree.FolderHierarchy.FirstOrDefault(x => x.Name == FolderName);
        var tmpFolder = await cache.GetCachedFolder(FolderId);    
        return block as FolderContent;
    }

    private async Task<FolderTreeContent> GetFolderTreeContentAsync()
    {
        if (blockCache.ContainsKey(FolderTreeId))
        {
            return blockCache[FolderTreeId] as FolderTreeContent;
        }
        var block = await rawBlockManager.ReadBlockAsync(FolderTreeId);
        if (block is null || block.Type != Models.BlockType.FolderTree)
        {
            return null;
        }
        blockCache.Add(FolderTreeId, block);
        return block as FolderTreeContent;
    }

    private async Task<MetadataContent> GetMetadataAsync()
    {
        if (blockCache.ContainsKey(MetadataId))
        {
            return blockCache[MetadataId] as MetadataContent;
        }
        var block = await rawBlockManager.ReadBlockAsync(MetadataId);
        if (block is null || block.Type != Models.BlockType.Metadata)
        {
            return null;
        }
        blockCache.Add(MetadataId, block);
        return block as MetadataContent;
    }
}
