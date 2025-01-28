using EmailDB.Format.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format;
public class MetadataManager
{
    private readonly BlockManager blockManager;
    private MetadataContent metadata;
    private readonly object metadataLock = new object();

    public MetadataManager(BlockManager blockManager)
    {
        this.blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        LoadMetadata();
    }

    private void LoadMetadata()
    {
        // Walk blocks to find latest metadata
        MetadataContent latest = null;
        foreach (var (_, block) in blockManager.WalkBlocks())
        {
            if (block.Content is MetadataContent metadataContent)
            {
                latest = metadataContent;
            }
        }

        metadata = latest ?? new MetadataContent();
    }

    public FolderTreeContent GetFolderTree()
    {
        if (metadata.FolderTreeOffset == -1)
        {
            return null;
        }

        var result = blockManager.TryReadBlockAt(metadata.FolderTreeOffset);
        if (result == null)
        {
            return null;
        }

        if (result.Value.Block.Content is not FolderTreeContent)
        {
            throw new InvalidOperationException("Folder tree offset does not point to a folder tree");
        }

        return (FolderTreeContent)result.Value.Block.Content;
    }

    public long WriteFolder(FolderContent folder)
    {
        var block = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.Folder,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = folder
        };

        return blockManager.WriteBlock(block);
    }

    public void WriteFolderTree(FolderTreeContent folderTree)
    {
        var block = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.FolderTree,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = folderTree
        };

        lock (metadataLock)
        {
            var offset = blockManager.WriteBlock(block);
            UpdateFolderTreeOffset(offset);
        }
    }

    public void UpdateFolderTreeOffset(long offset)
    {
        lock (metadataLock)
        {
            metadata.FolderTreeOffset = offset;
            WriteMetadata();
        }
    }

    private void WriteMetadata()
    {
        var block = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.Metadata,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = metadata
        };

        blockManager.WriteBlock(block);
    }
}