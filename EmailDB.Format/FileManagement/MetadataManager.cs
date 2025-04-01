using EmailDB.Format.Models;
using EmailDB.Format.Models.Blocks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.FileManagement;
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

    public void InitializeFile()
    {

        // Step 1: Write Metadata at Offset 0 (Ensuring it is 10MB)
        metadata = new MetadataContent();
        WriteMetadata();


        // Step 2: Write WAL block
        long actualWALOffset = blockManager.WriteBlock(new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.WAL,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = new WALContent()
        });

        // Step 4: Update Metadata with WAL offset
        metadata.WALOffset = actualWALOffset;
        WriteMetadata();
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

    public async Task<FolderTreeContent> GetFolderTree()
    {
        if (metadata.FolderTreeOffset == -1)
        {
            return null;
        }

        var result = await blockManager.ReadBlockAsync(metadata.FolderTreeOffset);
        if (result == null)
        {
            return null;
        }

        if (result.Content is not FolderTreeContent)
        {
            throw new InvalidOperationException("Folder tree offset does not point to a folder tree");
        }

        return (FolderTreeContent)result.Content;
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
        lock (metadataLock)
        {
            metadata.SerializeMetadata();
            var metadataBlock = new Block
            {
                Header = new BlockHeader
                {
                    Type = BlockType.Metadata,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Version = 1
                },
                Content = metadata
            };
            blockManager.WriteBlock(metadataBlock, 0);
        }
    }

    internal void AddOrUpdateSegmentOffset(string segmentId, long offset)
    {
        lock (metadataLock)
        {
            if (metadata.SegmentOffsets.ContainsKey(segmentId))
            {
                metadata.SegmentOffsets[segmentId] = offset;
            }
            else
            {
                metadata.SegmentOffsets.Add(segmentId, offset);
            }          
            WriteMetadata();
        }

    }
}