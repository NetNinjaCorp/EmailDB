using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmailDB.UnitTests.Models;
using Moq;

namespace EmailDB.UnitTests.Helpers;

public class MockRawBlockManager : IRawBlockManager
{
    private readonly Mock<IRawBlockManager> _mock;

    public MockRawBlockManager()
    {
        _mock = new Mock<IRawBlockManager>();
    }

    public Mock<IRawBlockManager> Mock => _mock;

    public Task<Block> ReadBlockAsync(long blockId, CancellationToken cancellationToken = default)
    {
        return _mock.Object.ReadBlockAsync(blockId, cancellationToken);
    }

    public Task<BlockLocation> WriteBlockAsync(Block block, CancellationToken cancellationToken = default)
    {
        return _mock.Object.WriteBlockAsync(block, cancellationToken);
    }

    public IReadOnlyDictionary<long, BlockLocation> GetBlockLocations()
    {
        return _mock.Object.GetBlockLocations();
    }

    public List<long> ScanFile()
    {
        return _mock.Object.ScanFile();
    }

    public Task CompactAsync(CancellationToken cancellationToken = default)
    {
        return _mock.Object.CompactAsync(cancellationToken);
    }

    public void Dispose()
    {
        _mock.Object.Dispose();
    }
}

public interface IRawBlockManager : IDisposable
{
    Task<Block> ReadBlockAsync(long blockId, CancellationToken cancellationToken = default);
    Task<BlockLocation> WriteBlockAsync(Block block, CancellationToken cancellationToken = default);
    IReadOnlyDictionary<long, BlockLocation> GetBlockLocations();
    List<long> ScanFile();
    Task CompactAsync(CancellationToken cancellationToken = default);
}