using FreeSpaceWatcher.Service.Etw;

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
}
