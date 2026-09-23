using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Service.Config;
using FreeSpaceWatcher.Service.Etw;
using FreeSpaceWatcher.Service.Writes;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeSpaceWatcher.Service.Tests.Etw;

public sealed class KernelEventRouterTests
{
    // IRP flag values from wdm.h.
    private const int IrpNoCache = 0x0001;
    private const int IrpPagingIo = 0x0002;
    private const int IrpSynchronousApi = 0x0004;
    private const int IrpBufferedIo = 0x0010;
    private const int IrpSynchronousPagingIo = 0x0040;
    private const int IrpWriteOperation = 0x0200;

    [Theory]
    [InlineData(IrpPagingIo)]
    [InlineData(IrpPagingIo | IrpNoCache | IrpSynchronousPagingIo)]
    [InlineData(IrpPagingIo | IrpNoCache | IrpWriteOperation)]
    public void IsPagingIo_PagingFlag_IsDropped(int ioFlags) => Assert.True(KernelEventRouter.IsPagingIo(ioFlags));

    [Theory]
    [InlineData(0)]
    [InlineData(IrpWriteOperation | IrpSynchronousApi)]
    [InlineData(IrpWriteOperation | IrpBufferedIo | IrpSynchronousApi)]
    [InlineData(IrpNoCache | IrpWriteOperation)]
    public void IsPagingIo_CallerWrite_IsKept(int ioFlags) => Assert.False(KernelEventRouter.IsPagingIo(ioFlags));

    [Fact]
    public void SetEndOfFile_FromSystem_DoesNotChangeWritersExtendBytes()
    {
        const int Writer = 1234;
        const int SystemPid = 4;
        const long Chunk = 1 << 20;
        const string FilePath = @"C:\data\growing.bin";
        using TempDirectory temp = new();
        ConfigService config = new(new ServicePaths(temp.Path), NullLogger<ConfigService>.Instance);
        WriteAggregatorProvider aggregators = new(config);
        KernelEventRouter router = new(aggregators, new DevicePathMapper(_ => null, TimeProvider.System), ownProcessId: 1);
        DateTime now = DateTime.Now;

        router.SetEndOfFile(Writer, now, @"\??\" + FilePath, Chunk);
        router.SetEndOfFile(Writer, now, @"\??\" + FilePath, 2 * Chunk);
        router.SetEndOfFile(SystemPid, now, @"\??\" + FilePath, 8 * Chunk);
        router.SetEndOfFile(Writer, now, @"\??\" + FilePath, 3 * Chunk);

        DriveWriteSnapshot snapshot = aggregators.Current.Snapshot('C', DateTimeOffset.Now, topProcesses: 1000, topFolders: 10, topFiles: 1000);
        ProcessWriteReport writer = Assert.Single(snapshot.Processes, p => p.ProcessId == Writer);
        Assert.Equal(2 * Chunk, writer.ExtendBytes);
        Assert.DoesNotContain(snapshot.Processes, p => p.ProcessId == SystemPid);
    }
}
