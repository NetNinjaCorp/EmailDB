using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Format;

/// <summary>
/// Append-only B+-tree index using copy-on-write semantics (CouchDB model).
/// All mutations append new blocks via RawBlockManager — existing blocks are never overwritten.
/// Only the root-to-leaf path is rewritten per mutation; unchanged subtrees are shared.
/// </summary>
public class BTreeIndex
{
    private readonly RawBlockManager _rawBlockManager;
    private readonly IBlockEncryptionProvider? _encryptionProvider;
    private IndexRoot? _currentRoot;
    private long _currentRootBlockOffset = -1;
    private readonly Dictionary<long, long> _offsetToBlockId = new();

    /// <summary>The current index root, or null if the tree is empty.</summary>
    public IndexRoot? CurrentRoot => _currentRoot;

    /// <summary>The encryption provider, or null if encryption is not configured.</summary>
    public IBlockEncryptionProvider? EncryptionProvider => _encryptionProvider;

    public BTreeIndex(RawBlockManager rawBlockManager, IndexRoot? existingRoot = null, long existingRootBlockOffset = -1, IBlockEncryptionProvider? encryptionProvider = null)
    {
        _rawBlockManager = rawBlockManager;
        _currentRoot = existingRoot;
        _currentRootBlockOffset = existingRootBlockOffset;
        _encryptionProvider = encryptionProvider;
    }

    /// <summary>
    /// Encrypts the block payload and stamps the key epoch into Flags if the
    /// encryption provider is configured and the block type is encryptable.
    /// Called immediately before writing a block to disk.
    /// </summary>
    private void ApplyEncryption(Block block)
    {
        if (_encryptionProvider is null || !_encryptionProvider.IsEnabled)
            return;
        if (!_encryptionProvider.ShouldEncrypt(block.Type))
            return;

        block.Payload = _encryptionProvider.Encrypt(block.Payload, block.Type, block.BlockId);
        block.Flags |= Block.FlagEncrypted;
        block.SetKeyEpoch((byte)_encryptionProvider.ActiveEpoch);
    }

    /// <summary>
    /// Decrypts the block payload using the key epoch from Flags if the block
    /// is encrypted and an encryption provider is available.
    /// Called after reading a block from disk, before deserialization.
    /// </summary>
    private void ApplyDecryption(Block block)
    {
        if (_encryptionProvider is null || !block.IsEncrypted)
            return;

        block.Payload = _encryptionProvider.Decrypt(block.Payload, block.Type, block.BlockId, block.KeyEpoch);
    }

    /// <summary>
    /// Performs a read-only point lookup for the given key.
    /// Navigates from root to leaf using binary search on internal node keys.
    /// Returns the matching LeafEntry or a not-found failure.
    /// </summary>
    public async Task<Result<LeafEntry>> LookupAsync(EmailHashedID key, CancellationToken ct = default)
    {
        if (_currentRoot == null)
            return Result<LeafEntry>.Failure("Tree is empty");

        var currentOffset = _currentRoot.RootNodeBlockOffset;

        // Navigate through internal nodes to reach the leaf
        for (int level = 0; level < _currentRoot.TreeHeight - 1; level++)
        {
            var nodeBlock = await ReadNodeBlockAtOffsetAsync(currentOffset, ct);
            if (nodeBlock.IsFailure)
                return Result<LeafEntry>.Failure($"Failed to read internal node at level {level}: {nodeBlock.Error}");

            var internalNode = BTreeNodeSerializer.DeserializeInternal(nodeBlock.Value.Payload);

            // Binary search: find the child to descend into
            int childIndex = 0;
            while (childIndex < internalNode.KeyCount && key.CompareTo(internalNode.Keys[childIndex]) >= 0)
                childIndex++;

            currentOffset = internalNode.ChildOffsets[childIndex];
        }

        // Read the leaf node
        var leafBlock = await ReadNodeBlockAtOffsetAsync(currentOffset, ct);
        if (leafBlock.IsFailure)
            return Result<LeafEntry>.Failure($"Failed to read leaf: {leafBlock.Error}");

        var leaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock.Value.Payload);

        // Scan entries for matching key
        for (int i = 0; i < leaf.EntryCount; i++)
        {
            if (leaf.Entries[i].Key.Equals(key))
                return Result<LeafEntry>.Success(leaf.Entries[i]);
        }

        return Result<LeafEntry>.Failure("Key not found");
    }

    /// <summary>
    /// Performs a range query returning all entries with keys in [startKey, endKey], sorted.
    /// Navigates to the leaf containing startKey, then scans forward across leaves
    /// using backtrack navigation through the internal node path.
    /// </summary>
    public async Task<Result<List<LeafEntry>>> RangeQueryAsync(EmailHashedID startKey, EmailHashedID endKey, CancellationToken ct = default)
    {
        if (_currentRoot == null)
            return Result<List<LeafEntry>>.Success(new List<LeafEntry>());

        if (startKey.CompareTo(endKey) > 0)
            return Result<List<LeafEntry>>.Failure("Start key must be <= end key");

        var results = new List<LeafEntry>();

        // Build the path from root to the leaf containing startKey
        var path = new List<(BTreeInternalNode Node, int ChildIndex)>();
        var currentOffset = _currentRoot.RootNodeBlockOffset;

        for (int level = 0; level < _currentRoot.TreeHeight - 1; level++)
        {
            var nodeBlock = await ReadNodeBlockAtOffsetAsync(currentOffset, ct);
            if (nodeBlock.IsFailure)
                return Result<List<LeafEntry>>.Failure($"Failed to read internal node at level {level}: {nodeBlock.Error}");

            var internalNode = BTreeNodeSerializer.DeserializeInternal(nodeBlock.Value.Payload);

            int childIndex = 0;
            while (childIndex < internalNode.KeyCount && startKey.CompareTo(internalNode.Keys[childIndex]) >= 0)
                childIndex++;

            path.Add((internalNode, childIndex));
            currentOffset = internalNode.ChildOffsets[childIndex];
        }

        // Scan leaves collecting entries in range
        while (true)
        {
            var leafBlock = await ReadNodeBlockAtOffsetAsync(currentOffset, ct);
            if (leafBlock.IsFailure)
                return Result<List<LeafEntry>>.Failure($"Failed to read leaf: {leafBlock.Error}");

            var leaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock.Value.Payload);

            bool doneScanning = false;
            for (int i = 0; i < leaf.EntryCount; i++)
            {
                if (leaf.Entries[i].Key.CompareTo(endKey) > 0)
                {
                    doneScanning = true;
                    break;
                }
                if (leaf.Entries[i].Key.CompareTo(startKey) >= 0)
                    results.Add(leaf.Entries[i]);
            }

            if (doneScanning)
                break;

            // Advance to the next leaf via backtrack navigation
            bool advanced = false;
            while (path.Count > 0)
            {
                var (parentNode, childIdx) = path[^1];
                path.RemoveAt(path.Count - 1);

                if (childIdx + 1 <= parentNode.KeyCount)
                {
                    int nextChildIdx = childIdx + 1;
                    path.Add((parentNode, nextChildIdx));
                    currentOffset = parentNode.ChildOffsets[nextChildIdx];

                    // Navigate down to the leftmost leaf from this child
                    for (int level = path.Count; level < _currentRoot.TreeHeight - 1; level++)
                    {
                        var nodeBlock = await ReadNodeBlockAtOffsetAsync(currentOffset, ct);
                        if (nodeBlock.IsFailure)
                            return Result<List<LeafEntry>>.Failure($"Failed to read internal node during range scan: {nodeBlock.Error}");

                        var internalNode = BTreeNodeSerializer.DeserializeInternal(nodeBlock.Value.Payload);
                        path.Add((internalNode, 0));
                        currentOffset = internalNode.ChildOffsets[0];
                    }

                    advanced = true;
                    break;
                }
            }

            if (!advanced)
                break;
        }

        return Result<List<LeafEntry>>.Success(results);
    }

    /// <summary>
    /// Inserts a key-value pair into the B+-tree.
    /// For an empty tree, creates a new root leaf node.
    /// </summary>
    public async Task<Result<IndexRoot>> InsertAsync(EmailHashedID key, long blockOffset, long blockId, CancellationToken ct = default)
    {
        if (_currentRoot == null)
        {
            return await CreateRootLeafAsync(key, blockOffset, blockId, ct);
        }

        // Read the current root node
        var rootBlock = await ReadNodeBlockAtOffsetAsync(_currentRoot.RootNodeBlockOffset, ct);
        if (rootBlock.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to read root node: {rootBlock.Error}");

        if (_currentRoot.TreeHeight == 1)
        {
            // Root is a leaf
            var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock.Value.Payload);
            return await InsertIntoLeafRootAsync(leaf, key, blockOffset, blockId, ct);
        }

        // Multi-level tree: navigate internal nodes to find target leaf
        return await InsertIntoMultiLevelTreeAsync(key, blockOffset, blockId, ct);
    }

    private async Task<Result<IndexRoot>> CreateRootLeafAsync(EmailHashedID key, long blockOffset, long blockId, CancellationToken ct)
    {
        // Create leaf node with a single entry
        var leaf = new BTreeLeafNode
        {
            NodeType = (byte)BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = 1,
            PrevChainHash = new byte[32], // Zero for first node in chain
            Entries = new[] { new LeafEntry { Key = key, BlockOffset = blockOffset, BlockId = blockId } }
        };

        // Compute BLAKE3 content hash
        leaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(leaf);

        // Serialize and write the leaf block
        var payload = BTreeNodeSerializer.SerializeLeaf(leaf);
        var leafBlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId();
        var leafBlock = new Block
        {
            Version = 1,
            Type = BlockType.BTreeLeaf,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = leafBlockId,
            Payload = payload
        };

        ApplyEncryption(leafBlock);
        var leafWriteResult = await _rawBlockManager.WriteBlockAsync(leafBlock, ct);
        if (leafWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write leaf block: {leafWriteResult.Error}");

        // Create and write the IndexRoot pointing to the new leaf
        var previousRootOffset = _currentRootBlockOffset;
        var previousRootHash = _currentRoot?.RootNodeHash ?? new byte[32];

        var indexRoot = new IndexRoot
        {
            RootNodeBlockOffset = leafWriteResult.Value.Position,
            EntryCount = 1,
            TreeHeight = 1,
            RootNodeHash = leaf.NodeContentHash,
            PreviousRootHash = previousRootHash,
            PreviousRootOffset = previousRootOffset
        };

        var rootPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoot);
        var rootBlockId = BlockIdGenerator.Instance.GetNextIndexRootId();
        var rootBlock = new Block
        {
            Version = 1,
            Type = BlockType.IndexRoot,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = rootBlockId,
            Payload = rootPayload
        };

        ApplyEncryption(rootBlock);
        var rootWriteResult = await _rawBlockManager.WriteBlockAsync(rootBlock, ct);
        if (rootWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write index root block: {rootWriteResult.Error}");

        _currentRoot = indexRoot;
        _currentRootBlockOffset = rootWriteResult.Value.Position;
        return Result<IndexRoot>.Success(indexRoot);
    }

    private async Task<Result<IndexRoot>> InsertIntoLeafRootAsync(BTreeLeafNode leaf, EmailHashedID key, long blockOffset, long blockId, CancellationToken ct)
    {
        // Check for duplicate key (upsert)
        for (int i = 0; i < leaf.EntryCount; i++)
        {
            if (leaf.Entries[i].Key.Equals(key))
            {
                // Update existing entry — create a new leaf with the updated value
                var updatedEntries = new LeafEntry[leaf.EntryCount];
                Array.Copy(leaf.Entries, updatedEntries, leaf.EntryCount);
                updatedEntries[i] = new LeafEntry { Key = key, BlockOffset = blockOffset, BlockId = blockId };

                var updatedLeaf = new BTreeLeafNode
                {
                    NodeType = (byte)BlockType.BTreeLeaf,
                    Version = 1,
                    EntryCount = leaf.EntryCount,
                    PrevChainHash = leaf.NodeContentHash, // Chain to previous version
                    Entries = updatedEntries
                };
                updatedLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(updatedLeaf);

                return await WriteLeafAsRootAsync(updatedLeaf, _currentRoot!.EntryCount, ct);
            }
        }

        if (leaf.EntryCount < BTreeLeafNode.MaxEntries)
        {
            // Room in the leaf — insert in sorted order
            var newEntries = new LeafEntry[leaf.EntryCount + 1];
            int insertPos = 0;
            while (insertPos < leaf.EntryCount && leaf.Entries[insertPos].Key.CompareTo(key) < 0)
                insertPos++;

            // Copy entries before insert position
            Array.Copy(leaf.Entries, 0, newEntries, 0, insertPos);
            newEntries[insertPos] = new LeafEntry { Key = key, BlockOffset = blockOffset, BlockId = blockId };
            // Copy entries after insert position
            if (insertPos < leaf.EntryCount)
                Array.Copy(leaf.Entries, insertPos, newEntries, insertPos + 1, leaf.EntryCount - insertPos);

            var newLeaf = new BTreeLeafNode
            {
                NodeType = (byte)BlockType.BTreeLeaf,
                Version = 1,
                EntryCount = (ushort)(leaf.EntryCount + 1),
                PrevChainHash = leaf.NodeContentHash,
                Entries = newEntries
            };
            newLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(newLeaf);

            return await WriteLeafAsRootAsync(newLeaf, _currentRoot!.EntryCount + 1, ct);
        }

        // Leaf is full — split into two leaves and create a new internal root
        return await SplitLeafRootAsync(leaf, key, blockOffset, blockId, ct);
    }

    private async Task<Result<IndexRoot>> SplitLeafRootAsync(BTreeLeafNode leaf, EmailHashedID key, long blockOffset, long blockId, CancellationToken ct)
    {
        // Merge all entries (existing full leaf + new entry) into a sorted array
        var allEntries = new LeafEntry[leaf.EntryCount + 1];
        int insertPos = 0;
        while (insertPos < leaf.EntryCount && leaf.Entries[insertPos].Key.CompareTo(key) < 0)
            insertPos++;

        Array.Copy(leaf.Entries, 0, allEntries, 0, insertPos);
        allEntries[insertPos] = new LeafEntry { Key = key, BlockOffset = blockOffset, BlockId = blockId };
        if (insertPos < leaf.EntryCount)
            Array.Copy(leaf.Entries, insertPos, allEntries, insertPos + 1, leaf.EntryCount - insertPos);

        // Split at midpoint
        int totalCount = allEntries.Length;
        int mid = totalCount / 2;

        // Left leaf: entries [0, mid)
        var leftEntries = new LeafEntry[mid];
        Array.Copy(allEntries, 0, leftEntries, 0, mid);

        var leftLeaf = new BTreeLeafNode
        {
            NodeType = (byte)BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)mid,
            PrevChainHash = leaf.NodeContentHash, // Chain to previous version of this leaf
            Entries = leftEntries
        };
        leftLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(leftLeaf);

        // Right leaf: entries [mid, totalCount)
        int rightCount = totalCount - mid;
        var rightEntries = new LeafEntry[rightCount];
        Array.Copy(allEntries, mid, rightEntries, 0, rightCount);

        var rightLeaf = new BTreeLeafNode
        {
            NodeType = (byte)BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)rightCount,
            PrevChainHash = new byte[32], // New node, no previous version
            Entries = rightEntries
        };
        rightLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(rightLeaf);

        // Write left leaf block
        var leftPayload = BTreeNodeSerializer.SerializeLeaf(leftLeaf);
        var leftBlock = new Block
        {
            Version = 1,
            Type = BlockType.BTreeLeaf,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId(),
            Payload = leftPayload
        };
        ApplyEncryption(leftBlock);
        var leftWriteResult = await _rawBlockManager.WriteBlockAsync(leftBlock, ct);
        if (leftWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write left leaf: {leftWriteResult.Error}");

        // Write right leaf block
        var rightPayload = BTreeNodeSerializer.SerializeLeaf(rightLeaf);
        var rightBlock = new Block
        {
            Version = 1,
            Type = BlockType.BTreeLeaf,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId(),
            Payload = rightPayload
        };
        ApplyEncryption(rightBlock);
        var rightWriteResult = await _rawBlockManager.WriteBlockAsync(rightBlock, ct);
        if (rightWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write right leaf: {rightWriteResult.Error}");

        // Create new internal root with separator key = first key of right leaf
        var separatorKey = rightEntries[0].Key;
        var internalNode = new BTreeInternalNode
        {
            NodeType = (byte)BlockType.BTreeInternal,
            Version = 1,
            KeyCount = 1,
            PrevChainHash = new byte[32], // First internal node, no previous version
            Keys = new[] { separatorKey },
            ChildOffsets = new[] { leftWriteResult.Value.Position, rightWriteResult.Value.Position },
            ChildHashes = new[] { leftLeaf.NodeContentHash, rightLeaf.NodeContentHash }
        };
        internalNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(internalNode);

        // Write internal node block
        var internalPayload = BTreeNodeSerializer.SerializeInternal(internalNode);
        var internalBlock = new Block
        {
            Version = 1,
            Type = BlockType.BTreeInternal,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBTreeInternalId(),
            Payload = internalPayload
        };
        ApplyEncryption(internalBlock);
        var internalWriteResult = await _rawBlockManager.WriteBlockAsync(internalBlock, ct);
        if (internalWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write internal node: {internalWriteResult.Error}");

        // Create new IndexRoot pointing to the internal node (height 2)
        var indexRoot = new IndexRoot
        {
            RootNodeBlockOffset = internalWriteResult.Value.Position,
            EntryCount = _currentRoot!.EntryCount + 1,
            TreeHeight = 2,
            RootNodeHash = internalNode.NodeContentHash,
            PreviousRootHash = _currentRoot.RootNodeHash,
            PreviousRootOffset = _currentRootBlockOffset
        };

        var rootPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoot);
        var rootBlock = new Block
        {
            Version = 1,
            Type = BlockType.IndexRoot,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextIndexRootId(),
            Payload = rootPayload
        };
        ApplyEncryption(rootBlock);
        var rootWriteResult = await _rawBlockManager.WriteBlockAsync(rootBlock, ct);
        if (rootWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write index root: {rootWriteResult.Error}");

        _currentRoot = indexRoot;
        _currentRootBlockOffset = rootWriteResult.Value.Position;
        return Result<IndexRoot>.Success(indexRoot);
    }

    private async Task<Result<IndexRoot>> WriteLeafAsRootAsync(BTreeLeafNode leaf, long totalEntryCount, CancellationToken ct)
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(leaf);
        var leafBlock = new Block
        {
            Version = 1,
            Type = BlockType.BTreeLeaf,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId(),
            Payload = payload
        };

        ApplyEncryption(leafBlock);
        var leafWriteResult = await _rawBlockManager.WriteBlockAsync(leafBlock, ct);
        if (leafWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write leaf block: {leafWriteResult.Error}");

        var indexRoot = new IndexRoot
        {
            RootNodeBlockOffset = leafWriteResult.Value.Position,
            EntryCount = totalEntryCount,
            TreeHeight = 1,
            RootNodeHash = leaf.NodeContentHash,
            PreviousRootHash = _currentRoot?.RootNodeHash ?? new byte[32],
            PreviousRootOffset = _currentRootBlockOffset
        };

        var rootPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoot);
        var rootBlock = new Block
        {
            Version = 1,
            Type = BlockType.IndexRoot,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextIndexRootId(),
            Payload = rootPayload
        };

        ApplyEncryption(rootBlock);
        var rootWriteResult = await _rawBlockManager.WriteBlockAsync(rootBlock, ct);
        if (rootWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write index root: {rootWriteResult.Error}");

        _currentRoot = indexRoot;
        _currentRootBlockOffset = rootWriteResult.Value.Position;
        return Result<IndexRoot>.Success(indexRoot);
    }

    private async Task<Result<Block>> ReadNodeBlockAtOffsetAsync(long offset, CancellationToken ct)
    {
        Result<Block> result;

        // Check offset→blockId cache first
        if (_offsetToBlockId.TryGetValue(offset, out long cachedBlockId))
        {
            result = await _rawBlockManager.ReadBlockAsync(cachedBlockId, ct);
            if (result.IsSuccess) ApplyDecryption(result.Value);
            return result;
        }

        // Cache miss: scan block locations and cache the result
        var locations = _rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            if (kvp.Value.Position == offset)
            {
                _offsetToBlockId[offset] = kvp.Key;
                result = await _rawBlockManager.ReadBlockAsync(kvp.Key, ct);
                if (result.IsSuccess) ApplyDecryption(result.Value);
                return result;
            }
        }
        return Result<Block>.Failure($"No block found at offset {offset}");
    }

    /// <summary>
    /// Deletes a key from the B+-tree using copy-on-write semantics.
    /// Creates a new leaf without the entry and cascades parent updates to a new root.
    /// The old leaf block remains in the file (append-only); compaction reclaims space.
    /// Returns the new IndexRoot on success, or a not-found failure if the key doesn't exist.
    /// </summary>
    public async Task<Result<IndexRoot>> DeleteAsync(EmailHashedID key, CancellationToken ct = default)
    {
        if (_currentRoot == null)
            return Result<IndexRoot>.Failure("Key not found");

        if (_currentRoot.TreeHeight == 1)
            return await DeleteFromLeafRootAsync(key, ct);

        return await DeleteFromMultiLevelTreeAsync(key, ct);
    }

    private async Task<Result<IndexRoot>> DeleteFromLeafRootAsync(EmailHashedID key, CancellationToken ct)
    {
        var rootBlock = await ReadNodeBlockAtOffsetAsync(_currentRoot!.RootNodeBlockOffset, ct);
        if (rootBlock.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to read root leaf: {rootBlock.Error}");

        var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock.Value.Payload);

        // Find the key to delete
        int deleteIdx = -1;
        for (int i = 0; i < leaf.EntryCount; i++)
        {
            if (leaf.Entries[i].Key.Equals(key)) { deleteIdx = i; break; }
        }

        if (deleteIdx < 0)
            return Result<IndexRoot>.Failure("Key not found");

        // Create new leaf without the deleted entry
        var newEntries = new LeafEntry[leaf.EntryCount - 1];
        if (deleteIdx > 0)
            Array.Copy(leaf.Entries, 0, newEntries, 0, deleteIdx);
        if (deleteIdx < leaf.EntryCount - 1)
            Array.Copy(leaf.Entries, deleteIdx + 1, newEntries, deleteIdx, leaf.EntryCount - deleteIdx - 1);

        var newLeaf = new BTreeLeafNode
        {
            NodeType = (byte)BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)(leaf.EntryCount - 1),
            PrevChainHash = leaf.NodeContentHash,
            Entries = newEntries
        };
        newLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(newLeaf);

        return await WriteLeafAsRootAsync(newLeaf, _currentRoot.EntryCount - 1, ct);
    }

    private async Task<Result<IndexRoot>> DeleteFromMultiLevelTreeAsync(EmailHashedID key, CancellationToken ct)
    {
        // Navigate from root to leaf, building the path
        var path = new List<(BTreeInternalNode Node, int ChildIndex)>();
        var currentNodeOffset = _currentRoot!.RootNodeBlockOffset;

        for (int level = 0; level < _currentRoot.TreeHeight - 1; level++)
        {
            var nodeBlock = await ReadNodeBlockAtOffsetAsync(currentNodeOffset, ct);
            if (nodeBlock.IsFailure)
                return Result<IndexRoot>.Failure($"Failed to read internal node at level {level}: {nodeBlock.Error}");

            var internalNode = BTreeNodeSerializer.DeserializeInternal(nodeBlock.Value.Payload);

            int childIndex = 0;
            while (childIndex < internalNode.KeyCount && key.CompareTo(internalNode.Keys[childIndex]) >= 0)
                childIndex++;

            path.Add((internalNode, childIndex));
            currentNodeOffset = internalNode.ChildOffsets[childIndex];
        }

        // Read the target leaf
        var leafBlock = await ReadNodeBlockAtOffsetAsync(currentNodeOffset, ct);
        if (leafBlock.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to read leaf: {leafBlock.Error}");

        var leaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock.Value.Payload);

        // Find the key
        int deleteIdx = -1;
        for (int i = 0; i < leaf.EntryCount; i++)
        {
            if (leaf.Entries[i].Key.Equals(key)) { deleteIdx = i; break; }
        }

        if (deleteIdx < 0)
            return Result<IndexRoot>.Failure("Key not found");

        // Create new leaf without the deleted entry
        var newEntries = new LeafEntry[leaf.EntryCount - 1];
        if (deleteIdx > 0)
            Array.Copy(leaf.Entries, 0, newEntries, 0, deleteIdx);
        if (deleteIdx < leaf.EntryCount - 1)
            Array.Copy(leaf.Entries, deleteIdx + 1, newEntries, deleteIdx, leaf.EntryCount - deleteIdx - 1);

        var newLeaf = new BTreeLeafNode
        {
            NodeType = (byte)BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)(leaf.EntryCount - 1),
            PrevChainHash = leaf.NodeContentHash,
            Entries = newEntries
        };
        newLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(newLeaf);

        // Check for underflow — merge with sibling via parent if below minimum fill
        if (newLeaf.EntryCount < BTreeLeafNode.MinEntries && path.Count > 0)
        {
            var (parentNode, childIdx) = path[^1];

            // Choose sibling: prefer right, fallback to left
            int siblingIdx = childIdx < parentNode.KeyCount ? childIdx + 1 : childIdx - 1;

            if (siblingIdx >= 0 && siblingIdx <= parentNode.KeyCount)
            {
                var siblingBlock = await ReadNodeBlockAtOffsetAsync(parentNode.ChildOffsets[siblingIdx], ct);
                if (siblingBlock.IsSuccess)
                {
                    var siblingLeaf = BTreeNodeSerializer.DeserializeLeaf(siblingBlock.Value.Payload);
                    int totalEntries = newLeaf.EntryCount + siblingLeaf.EntryCount;

                    if (totalEntries <= BTreeLeafNode.MaxEntries)
                    {
                        return await MergeLeavesAsync(newLeaf, siblingLeaf,
                            childIdx, siblingIdx, path, ct);
                    }
                }
            }
        }

        // No underflow or merge not possible: write the leaf and cascade parent updates
        var leafWriteResult = await WriteLeafBlockAsync(newLeaf, ct);
        if (leafWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write leaf: {leafWriteResult.Error}");

        // Cascade updated child offset+hash through internal nodes (copy-on-write)
        long bubbleOffset = leafWriteResult.Value.Position;
        byte[] bubbleHash = newLeaf.NodeContentHash;

        for (int i = path.Count - 1; i >= 0; i--)
        {
            var (node, childIdx) = path[i];
            var newNode = CloneInternalWithUpdatedChild(node, childIdx, bubbleOffset, bubbleHash);
            var wr = await WriteInternalBlockAsync(newNode, ct);
            if (wr.IsFailure)
                return Result<IndexRoot>.Failure($"Failed to write internal node: {wr.Error}");
            bubbleOffset = wr.Value.Position;
            bubbleHash = newNode.NodeContentHash;
        }

        return await WriteNewIndexRootAsync(bubbleOffset, bubbleHash,
            _currentRoot.TreeHeight, _currentRoot.EntryCount - 1, ct);
    }

    private async Task<Result<IndexRoot>> MergeLeavesAsync(
        BTreeLeafNode underflowLeaf, BTreeLeafNode siblingLeaf,
        int underflowChildIdx, int siblingChildIdx,
        List<(BTreeInternalNode Node, int ChildIndex)> path,
        CancellationToken ct)
    {
        // Determine left/right by position — entries are already sorted within each leaf
        // and left entries < separator < right entries, so concatenation preserves sort order
        BTreeLeafNode leftLeaf, rightLeaf;
        if (siblingChildIdx < underflowChildIdx)
        {
            leftLeaf = siblingLeaf;
            rightLeaf = underflowLeaf;
        }
        else
        {
            leftLeaf = underflowLeaf;
            rightLeaf = siblingLeaf;
        }

        int totalEntries = leftLeaf.EntryCount + rightLeaf.EntryCount;
        var mergedEntries = new LeafEntry[totalEntries];
        Array.Copy(leftLeaf.Entries, 0, mergedEntries, 0, leftLeaf.EntryCount);
        Array.Copy(rightLeaf.Entries, 0, mergedEntries, leftLeaf.EntryCount, rightLeaf.EntryCount);

        var mergedLeaf = new BTreeLeafNode
        {
            NodeType = (byte)BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)totalEntries,
            PrevChainHash = underflowLeaf.NodeContentHash,
            Entries = mergedEntries
        };
        mergedLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(mergedLeaf);

        var mergedWriteResult = await WriteLeafBlockAsync(mergedLeaf, ct);
        if (mergedWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write merged leaf: {mergedWriteResult.Error}");

        // Update parent: remove the separator key and one child
        var (parentNode, _) = path[^1];
        int removeKeyIdx = Math.Min(underflowChildIdx, siblingChildIdx);
        var newParent = RemoveChildFromInternal(parentNode, removeKeyIdx,
            mergedWriteResult.Value.Position, mergedLeaf.NodeContentHash);

        // Root collapse: parent has 0 keys (single child remaining)
        if (newParent.KeyCount == 0)
        {
            // The merged leaf becomes the new root — tree height decreases by 1
            if (path.Count == 1)
            {
                return await WriteNewIndexRootAsync(
                    mergedWriteResult.Value.Position,
                    mergedLeaf.NodeContentHash,
                    (ushort)(_currentRoot!.TreeHeight - 1),
                    _currentRoot.EntryCount - 1, ct);
            }

            // Parent collapsed but is not the root — point grandparent directly to merged child
            long bubbleOffset = mergedWriteResult.Value.Position;
            byte[] bubbleHash = mergedLeaf.NodeContentHash;

            for (int i = path.Count - 2; i >= 0; i--)
            {
                var (node, childIdx) = path[i];
                var updatedNode = CloneInternalWithUpdatedChild(node, childIdx, bubbleOffset, bubbleHash);
                var wr = await WriteInternalBlockAsync(updatedNode, ct);
                if (wr.IsFailure)
                    return Result<IndexRoot>.Failure($"Failed to write internal node: {wr.Error}");
                bubbleOffset = wr.Value.Position;
                bubbleHash = updatedNode.NodeContentHash;
            }

            return await WriteNewIndexRootAsync(bubbleOffset, bubbleHash,
                (ushort)(_currentRoot!.TreeHeight - 1), _currentRoot.EntryCount - 1, ct);
        }

        // Parent still has keys — write it and cascade up
        var parentWriteResult = await WriteInternalBlockAsync(newParent, ct);
        if (parentWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write parent: {parentWriteResult.Error}");

        long parentBubbleOffset = parentWriteResult.Value.Position;
        byte[] parentBubbleHash = newParent.NodeContentHash;

        for (int i = path.Count - 2; i >= 0; i--)
        {
            var (node, childIdx) = path[i];
            var updatedNode = CloneInternalWithUpdatedChild(node, childIdx, parentBubbleOffset, parentBubbleHash);
            var wr = await WriteInternalBlockAsync(updatedNode, ct);
            if (wr.IsFailure)
                return Result<IndexRoot>.Failure($"Failed to write internal node: {wr.Error}");
            parentBubbleOffset = wr.Value.Position;
            parentBubbleHash = updatedNode.NodeContentHash;
        }

        return await WriteNewIndexRootAsync(parentBubbleOffset, parentBubbleHash,
            _currentRoot!.TreeHeight, _currentRoot.EntryCount - 1, ct);
    }

    private BTreeInternalNode RemoveChildFromInternal(
        BTreeInternalNode node, int removeKeyIdx,
        long mergedChildOffset, byte[] mergedChildHash)
    {
        int newKeyCount = node.KeyCount - 1;
        var newKeys = new EmailHashedID[Math.Max(newKeyCount, 0)];
        var newOffsets = new long[newKeyCount + 1];
        var newHashes = new byte[newKeyCount + 1][];

        // Copy keys, skipping the removed separator
        int ki = 0;
        for (int i = 0; i < node.KeyCount; i++)
        {
            if (i == removeKeyIdx) continue;
            newKeys[ki++] = node.Keys[i];
        }

        // Copy children: merged child at removeKeyIdx, skip removeKeyIdx + 1
        int ci = 0;
        for (int i = 0; i <= node.KeyCount; i++)
        {
            if (i == removeKeyIdx)
            {
                newOffsets[ci] = mergedChildOffset;
                newHashes[ci] = mergedChildHash;
                ci++;
            }
            else if (i == removeKeyIdx + 1)
            {
                continue; // Absorbed into merged child
            }
            else
            {
                newOffsets[ci] = node.ChildOffsets[i];
                newHashes[ci] = node.ChildHashes[i];
                ci++;
            }
        }

        var newNode = new BTreeInternalNode
        {
            NodeType = (byte)BlockType.BTreeInternal,
            Version = 1,
            KeyCount = (ushort)newKeyCount,
            PrevChainHash = node.NodeContentHash,
            Keys = newKeys,
            ChildOffsets = newOffsets,
            ChildHashes = newHashes
        };
        newNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(newNode);
        return newNode;
    }

    #region Multi-level insert

    private async Task<Result<IndexRoot>> InsertIntoMultiLevelTreeAsync(
        EmailHashedID key, long blockOffset, long blockId, CancellationToken ct)
    {
        // Step 1: Navigate from root to leaf, building the path
        var path = new List<(BTreeInternalNode Node, int ChildIndex)>();
        var currentNodeOffset = _currentRoot!.RootNodeBlockOffset;

        for (int level = 0; level < _currentRoot.TreeHeight - 1; level++)
        {
            var nodeBlock = await ReadNodeBlockAtOffsetAsync(currentNodeOffset, ct);
            if (nodeBlock.IsFailure)
                return Result<IndexRoot>.Failure($"Failed to read internal node at level {level}: {nodeBlock.Error}");

            var internalNode = BTreeNodeSerializer.DeserializeInternal(nodeBlock.Value.Payload);

            // Find which child to descend into
            int childIndex = 0;
            while (childIndex < internalNode.KeyCount && key.CompareTo(internalNode.Keys[childIndex]) >= 0)
                childIndex++;

            path.Add((internalNode, childIndex));
            currentNodeOffset = internalNode.ChildOffsets[childIndex];
        }

        // Step 2: Read the target leaf
        var leafBlock = await ReadNodeBlockAtOffsetAsync(currentNodeOffset, ct);
        if (leafBlock.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to read leaf: {leafBlock.Error}");

        var leaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock.Value.Payload);

        // Step 3: Insert into leaf, tracking bubble-up state
        long newEntryCount = _currentRoot.EntryCount;
        long bubbleOffset = -1;
        byte[]? bubbleHash = null;
        bool hasSplit = false;
        EmailHashedID splitSeparator = default;
        long splitLeftOffset = 0, splitRightOffset = 0;
        byte[]? splitLeftHash = null, splitRightHash = null;

        // Check for duplicate key (upsert)
        int dupIdx = -1;
        for (int i = 0; i < leaf.EntryCount; i++)
        {
            if (leaf.Entries[i].Key.Equals(key)) { dupIdx = i; break; }
        }

        if (dupIdx >= 0)
        {
            // Upsert: create new leaf with updated entry
            var updatedEntries = new LeafEntry[leaf.EntryCount];
            Array.Copy(leaf.Entries, updatedEntries, leaf.EntryCount);
            updatedEntries[dupIdx] = new LeafEntry { Key = key, BlockOffset = blockOffset, BlockId = blockId };

            var updatedLeaf = new BTreeLeafNode
            {
                NodeType = (byte)BlockType.BTreeLeaf,
                Version = 1,
                EntryCount = leaf.EntryCount,
                PrevChainHash = leaf.NodeContentHash,
                Entries = updatedEntries
            };
            updatedLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(updatedLeaf);

            var wr = await WriteLeafBlockAsync(updatedLeaf, ct);
            if (wr.IsFailure)
                return Result<IndexRoot>.Failure($"Failed to write updated leaf: {wr.Error}");
            bubbleOffset = wr.Value.Position;
            bubbleHash = updatedLeaf.NodeContentHash;
        }
        else if (leaf.EntryCount < BTreeLeafNode.MaxEntries)
        {
            // Room in leaf — insert in sorted order
            newEntryCount++;
            var newLeaf = CreateLeafWithInsert(leaf, key, blockOffset, blockId);

            var wr = await WriteLeafBlockAsync(newLeaf, ct);
            if (wr.IsFailure)
                return Result<IndexRoot>.Failure($"Failed to write leaf: {wr.Error}");
            bubbleOffset = wr.Value.Position;
            bubbleHash = newLeaf.NodeContentHash;
        }
        else
        {
            // Leaf is full — split
            newEntryCount++;
            var (leftLeaf, rightLeaf, sepKey) = SplitLeafWithInsert(leaf, key, blockOffset, blockId);

            var leftWr = await WriteLeafBlockAsync(leftLeaf, ct);
            if (leftWr.IsFailure)
                return Result<IndexRoot>.Failure($"Failed to write left leaf: {leftWr.Error}");
            var rightWr = await WriteLeafBlockAsync(rightLeaf, ct);
            if (rightWr.IsFailure)
                return Result<IndexRoot>.Failure($"Failed to write right leaf: {rightWr.Error}");

            hasSplit = true;
            splitSeparator = sepKey;
            splitLeftOffset = leftWr.Value.Position;
            splitLeftHash = leftLeaf.NodeContentHash;
            splitRightOffset = rightWr.Value.Position;
            splitRightHash = rightLeaf.NodeContentHash;
        }

        // Step 4: Propagate up through internal nodes
        for (int i = path.Count - 1; i >= 0; i--)
        {
            var (node, childIdx) = path[i];

            if (!hasSplit)
            {
                // No split: update child offset+hash, rewrite node (copy-on-write)
                var newNode = CloneInternalWithUpdatedChild(node, childIdx, bubbleOffset, bubbleHash!);
                var wr = await WriteInternalBlockAsync(newNode, ct);
                if (wr.IsFailure)
                    return Result<IndexRoot>.Failure($"Failed to write internal node: {wr.Error}");
                bubbleOffset = wr.Value.Position;
                bubbleHash = newNode.NodeContentHash;
            }
            else
            {
                if (node.KeyCount < BTreeInternalNode.MaxKeys)
                {
                    // Room in internal node: insert new key+child
                    var newNode = InsertIntoInternalNode(node, childIdx,
                        splitSeparator, splitLeftOffset, splitLeftHash!, splitRightOffset, splitRightHash!);
                    var wr = await WriteInternalBlockAsync(newNode, ct);
                    if (wr.IsFailure)
                        return Result<IndexRoot>.Failure($"Failed to write internal node: {wr.Error}");

                    hasSplit = false;
                    bubbleOffset = wr.Value.Position;
                    bubbleHash = newNode.NodeContentHash;
                }
                else
                {
                    // Internal node is full — split it and continue bubbling up
                    var (leftInt, rightInt, promoted) = SplitInternalWithInsert(node, childIdx,
                        splitSeparator, splitLeftOffset, splitLeftHash!, splitRightOffset, splitRightHash!);

                    var leftWr = await WriteInternalBlockAsync(leftInt, ct);
                    if (leftWr.IsFailure)
                        return Result<IndexRoot>.Failure($"Failed to write left internal: {leftWr.Error}");
                    var rightWr = await WriteInternalBlockAsync(rightInt, ct);
                    if (rightWr.IsFailure)
                        return Result<IndexRoot>.Failure($"Failed to write right internal: {rightWr.Error}");

                    splitSeparator = promoted;
                    splitLeftOffset = leftWr.Value.Position;
                    splitLeftHash = leftInt.NodeContentHash;
                    splitRightOffset = rightWr.Value.Position;
                    splitRightHash = rightInt.NodeContentHash;
                    // hasSplit remains true
                }
            }
        }

        // Step 5: Write new IndexRoot
        if (!hasSplit)
        {
            return await WriteNewIndexRootAsync(bubbleOffset, bubbleHash!,
                _currentRoot.TreeHeight, newEntryCount, ct);
        }
        else
        {
            // Root split: create new internal root node, increase height
            var newRootNode = new BTreeInternalNode
            {
                NodeType = (byte)BlockType.BTreeInternal,
                Version = 1,
                KeyCount = 1,
                PrevChainHash = new byte[32],
                Keys = new[] { splitSeparator },
                ChildOffsets = new[] { splitLeftOffset, splitRightOffset },
                ChildHashes = new[] { splitLeftHash!, splitRightHash! }
            };
            newRootNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(newRootNode);

            var rootWr = await WriteInternalBlockAsync(newRootNode, ct);
            if (rootWr.IsFailure)
                return Result<IndexRoot>.Failure($"Failed to write new root: {rootWr.Error}");

            return await WriteNewIndexRootAsync(rootWr.Value.Position, newRootNode.NodeContentHash,
                (ushort)(_currentRoot.TreeHeight + 1), newEntryCount, ct);
        }
    }

    private BTreeLeafNode CreateLeafWithInsert(BTreeLeafNode leaf, EmailHashedID key, long blockOffset, long blockId)
    {
        var newEntries = new LeafEntry[leaf.EntryCount + 1];
        int insertPos = 0;
        while (insertPos < leaf.EntryCount && leaf.Entries[insertPos].Key.CompareTo(key) < 0)
            insertPos++;

        Array.Copy(leaf.Entries, 0, newEntries, 0, insertPos);
        newEntries[insertPos] = new LeafEntry { Key = key, BlockOffset = blockOffset, BlockId = blockId };
        if (insertPos < leaf.EntryCount)
            Array.Copy(leaf.Entries, insertPos, newEntries, insertPos + 1, leaf.EntryCount - insertPos);

        var newLeaf = new BTreeLeafNode
        {
            NodeType = (byte)BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)(leaf.EntryCount + 1),
            PrevChainHash = leaf.NodeContentHash,
            Entries = newEntries
        };
        newLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(newLeaf);
        return newLeaf;
    }

    private (BTreeLeafNode Left, BTreeLeafNode Right, EmailHashedID SeparatorKey) SplitLeafWithInsert(
        BTreeLeafNode leaf, EmailHashedID key, long blockOffset, long blockId)
    {
        var allEntries = new LeafEntry[leaf.EntryCount + 1];
        int insertPos = 0;
        while (insertPos < leaf.EntryCount && leaf.Entries[insertPos].Key.CompareTo(key) < 0)
            insertPos++;

        Array.Copy(leaf.Entries, 0, allEntries, 0, insertPos);
        allEntries[insertPos] = new LeafEntry { Key = key, BlockOffset = blockOffset, BlockId = blockId };
        if (insertPos < leaf.EntryCount)
            Array.Copy(leaf.Entries, insertPos, allEntries, insertPos + 1, leaf.EntryCount - insertPos);

        int totalCount = allEntries.Length;
        int mid = totalCount / 2;

        var leftEntries = new LeafEntry[mid];
        Array.Copy(allEntries, 0, leftEntries, 0, mid);
        var leftLeaf = new BTreeLeafNode
        {
            NodeType = (byte)BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)mid,
            PrevChainHash = leaf.NodeContentHash,
            Entries = leftEntries
        };
        leftLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(leftLeaf);

        int rightCount = totalCount - mid;
        var rightEntries = new LeafEntry[rightCount];
        Array.Copy(allEntries, mid, rightEntries, 0, rightCount);
        var rightLeaf = new BTreeLeafNode
        {
            NodeType = (byte)BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)rightCount,
            PrevChainHash = new byte[32],
            Entries = rightEntries
        };
        rightLeaf.NodeContentHash = BTreeHasher.ComputeLeafContentHash(rightLeaf);

        return (leftLeaf, rightLeaf, rightEntries[0].Key);
    }

    private BTreeInternalNode CloneInternalWithUpdatedChild(
        BTreeInternalNode node, int childIndex, long newOffset, byte[] newHash)
    {
        var newOffsets = (long[])node.ChildOffsets.Clone();
        newOffsets[childIndex] = newOffset;

        var newHashes = new byte[node.KeyCount + 1][];
        for (int i = 0; i < node.KeyCount + 1; i++)
            newHashes[i] = i == childIndex ? newHash : node.ChildHashes[i];

        var newNode = new BTreeInternalNode
        {
            NodeType = (byte)BlockType.BTreeInternal,
            Version = 1,
            KeyCount = node.KeyCount,
            PrevChainHash = node.NodeContentHash,
            Keys = (EmailHashedID[])node.Keys.Clone(),
            ChildOffsets = newOffsets,
            ChildHashes = newHashes
        };
        newNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(newNode);
        return newNode;
    }

    private BTreeInternalNode InsertIntoInternalNode(
        BTreeInternalNode node, int childIndex,
        EmailHashedID separatorKey, long leftOffset, byte[] leftHash, long rightOffset, byte[] rightHash)
    {
        int newKeyCount = node.KeyCount + 1;
        var newKeys = new EmailHashedID[newKeyCount];
        var newOffsets = new long[newKeyCount + 1];
        var newHashes = new byte[newKeyCount + 1][];

        // Insert separator key at childIndex
        Array.Copy(node.Keys, 0, newKeys, 0, childIndex);
        newKeys[childIndex] = separatorKey;
        if (childIndex < node.KeyCount)
            Array.Copy(node.Keys, childIndex, newKeys, childIndex + 1, node.KeyCount - childIndex);

        // Left child replaces original child, right child inserted after
        for (int i = 0; i < childIndex; i++)
        {
            newOffsets[i] = node.ChildOffsets[i];
            newHashes[i] = node.ChildHashes[i];
        }
        newOffsets[childIndex] = leftOffset;
        newHashes[childIndex] = leftHash;
        newOffsets[childIndex + 1] = rightOffset;
        newHashes[childIndex + 1] = rightHash;
        for (int i = childIndex + 1; i <= node.KeyCount; i++)
        {
            newOffsets[i + 1] = node.ChildOffsets[i];
            newHashes[i + 1] = node.ChildHashes[i];
        }

        var newNode = new BTreeInternalNode
        {
            NodeType = (byte)BlockType.BTreeInternal,
            Version = 1,
            KeyCount = (ushort)newKeyCount,
            PrevChainHash = node.NodeContentHash,
            Keys = newKeys,
            ChildOffsets = newOffsets,
            ChildHashes = newHashes
        };
        newNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(newNode);
        return newNode;
    }

    private (BTreeInternalNode Left, BTreeInternalNode Right, EmailHashedID PromotedKey) SplitInternalWithInsert(
        BTreeInternalNode node, int childIndex,
        EmailHashedID separatorKey, long leftOffset, byte[] leftHash, long rightOffset, byte[] rightHash)
    {
        // Create merged arrays (one more key + one more child than the full node)
        int tempKeyCount = node.KeyCount + 1;
        var tempKeys = new EmailHashedID[tempKeyCount];
        var tempOffsets = new long[tempKeyCount + 1];
        var tempHashes = new byte[tempKeyCount + 1][];

        Array.Copy(node.Keys, 0, tempKeys, 0, childIndex);
        tempKeys[childIndex] = separatorKey;
        if (childIndex < node.KeyCount)
            Array.Copy(node.Keys, childIndex, tempKeys, childIndex + 1, node.KeyCount - childIndex);

        for (int i = 0; i < childIndex; i++)
        {
            tempOffsets[i] = node.ChildOffsets[i];
            tempHashes[i] = node.ChildHashes[i];
        }
        tempOffsets[childIndex] = leftOffset;
        tempHashes[childIndex] = leftHash;
        tempOffsets[childIndex + 1] = rightOffset;
        tempHashes[childIndex + 1] = rightHash;
        for (int i = childIndex + 1; i <= node.KeyCount; i++)
        {
            tempOffsets[i + 1] = node.ChildOffsets[i];
            tempHashes[i + 1] = node.ChildHashes[i];
        }

        // Split at midpoint: promote middle key
        int mid = tempKeyCount / 2;
        var promotedKey = tempKeys[mid];

        // Left: keys [0, mid), children [0, mid]
        int leftKeyCount = mid;
        var leftKeys = new EmailHashedID[leftKeyCount];
        Array.Copy(tempKeys, 0, leftKeys, 0, leftKeyCount);
        var leftOffsets = new long[leftKeyCount + 1];
        var leftHashes = new byte[leftKeyCount + 1][];
        for (int i = 0; i <= leftKeyCount; i++)
        {
            leftOffsets[i] = tempOffsets[i];
            leftHashes[i] = tempHashes[i];
        }
        var leftNode = new BTreeInternalNode
        {
            NodeType = (byte)BlockType.BTreeInternal,
            Version = 1,
            KeyCount = (ushort)leftKeyCount,
            PrevChainHash = node.NodeContentHash,
            Keys = leftKeys,
            ChildOffsets = leftOffsets,
            ChildHashes = leftHashes
        };
        leftNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(leftNode);

        // Right: keys [mid+1, end), children [mid+1, end]
        int rightKeyCount = tempKeyCount - mid - 1;
        var rightKeys = new EmailHashedID[rightKeyCount];
        Array.Copy(tempKeys, mid + 1, rightKeys, 0, rightKeyCount);
        var rightOffsets = new long[rightKeyCount + 1];
        var rightHashes = new byte[rightKeyCount + 1][];
        for (int i = 0; i <= rightKeyCount; i++)
        {
            rightOffsets[i] = tempOffsets[mid + 1 + i];
            rightHashes[i] = tempHashes[mid + 1 + i];
        }
        var rightNode = new BTreeInternalNode
        {
            NodeType = (byte)BlockType.BTreeInternal,
            Version = 1,
            KeyCount = (ushort)rightKeyCount,
            PrevChainHash = new byte[32],
            Keys = rightKeys,
            ChildOffsets = rightOffsets,
            ChildHashes = rightHashes
        };
        rightNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(rightNode);

        return (leftNode, rightNode, promotedKey);
    }

    private async Task<Result<BlockLocation>> WriteLeafBlockAsync(BTreeLeafNode leaf, CancellationToken ct)
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(leaf);
        var block = new Block
        {
            Version = 1,
            Type = BlockType.BTreeLeaf,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId(),
            Payload = payload
        };
        ApplyEncryption(block);
        var result = await _rawBlockManager.WriteBlockAsync(block, ct);
        if (result.IsSuccess)
            _offsetToBlockId[result.Value.Position] = block.BlockId;
        return result;
    }

    private async Task<Result<BlockLocation>> WriteInternalBlockAsync(BTreeInternalNode node, CancellationToken ct)
    {
        var payload = BTreeNodeSerializer.SerializeInternal(node);
        var block = new Block
        {
            Version = 1,
            Type = BlockType.BTreeInternal,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBTreeInternalId(),
            Payload = payload
        };
        ApplyEncryption(block);
        var result = await _rawBlockManager.WriteBlockAsync(block, ct);
        if (result.IsSuccess)
            _offsetToBlockId[result.Value.Position] = block.BlockId;
        return result;
    }

    private async Task<Result<IndexRoot>> WriteNewIndexRootAsync(
        long rootNodeOffset, byte[] rootNodeHash, ushort treeHeight, long totalEntryCount, CancellationToken ct)
    {
        var indexRoot = new IndexRoot
        {
            RootNodeBlockOffset = rootNodeOffset,
            EntryCount = totalEntryCount,
            TreeHeight = treeHeight,
            RootNodeHash = rootNodeHash,
            PreviousRootHash = _currentRoot?.RootNodeHash ?? new byte[32],
            PreviousRootOffset = _currentRootBlockOffset
        };

        var rootPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoot);
        var rootBlock = new Block
        {
            Version = 1,
            Type = BlockType.IndexRoot,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextIndexRootId(),
            Payload = rootPayload
        };

        ApplyEncryption(rootBlock);
        var rootWriteResult = await _rawBlockManager.WriteBlockAsync(rootBlock, ct);
        if (rootWriteResult.IsFailure)
            return Result<IndexRoot>.Failure($"Failed to write index root: {rootWriteResult.Error}");

        _offsetToBlockId[rootWriteResult.Value.Position] = rootBlock.BlockId;
        _currentRoot = indexRoot;
        _currentRootBlockOffset = rootWriteResult.Value.Position;
        return Result<IndexRoot>.Success(indexRoot);
    }

    #endregion
}
