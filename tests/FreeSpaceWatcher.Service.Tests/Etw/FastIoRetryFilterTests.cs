using FreeSpaceWatcher.Service.Etw;

namespace FreeSpaceWatcher.Service.Tests.Etw;

public sealed class FastIoRetryFilterTests
{
    // IoFlags of a traced cached write: 0 on the fast-I/O attempt, 0x60A00 on the IRP that retries it.
    private const int FastIoAttempt = 0x0;
    private const int IrpRetry = 0x60A00;
    private const int Pid = 1234;
    private const int OtherPid = 5678;
    private const int Tid = 42;
    private const ulong FileObject = 0xFFFF_A001_2345_6780;
    private const long Offset = 64L << 20;
    private const int Size = 1 << 20;

    [Fact]
    public void IsRetry_IrpWriteRepeatingFastIoAttemptOnSameThread_IsDropped()
    {
        FastIoRetryFilter filter = new();

        Assert.False(filter.IsRetry(Pid, Tid, FileObject, Offset, Size, FastIoAttempt));
        Assert.True(filter.IsRetry(Pid, Tid, FileObject, Offset, Size, IrpRetry));
    }

    [Fact]
    public void IsRetry_IrpWriteWithoutFastIoAttempt_IsKept()
    {
        FastIoRetryFilter filter = new();

        Assert.False(filter.IsRetry(Pid, Tid, FileObject, 0, Size, IrpRetry));
        Assert.False(filter.IsRetry(Pid, Tid, FileObject, Size, Size, FastIoAttempt));
        Assert.True(filter.IsRetry(Pid, Tid, FileObject, Size, Size, IrpRetry));
        Assert.False(filter.IsRetry(Pid, Tid, FileObject, Size, Size, IrpRetry));
    }

    [Fact]
    public void IsRetry_TwoFastIoWritesToSameOffset_AreBothKept()
    {
        FastIoRetryFilter filter = new();

        Assert.False(filter.IsRetry(Pid, Tid, FileObject, Offset, Size, FastIoAttempt));
        Assert.False(filter.IsRetry(Pid, Tid, FileObject, Offset, Size, FastIoAttempt));
    }

    [Fact]
    public void IsRetry_IrpWriteOnAnotherThreadOrOffset_IsKept()
    {
        FastIoRetryFilter filter = new();

        Assert.False(filter.IsRetry(Pid, Tid, FileObject, Offset, Size, FastIoAttempt));
        Assert.False(filter.IsRetry(Pid, Tid + 1, FileObject, Offset, Size, IrpRetry));
        Assert.False(filter.IsRetry(Pid, Tid, FileObject, Offset + Size, Size, IrpRetry));
        Assert.False(filter.IsRetry(Pid, Tid, FileObject, Offset, Size, IrpRetry));
    }

    [Fact]
    public void ProcessEnded_ForgetsOnlyThatProcessesAttempts()
    {
        FastIoRetryFilter filter = new();
        filter.IsRetry(Pid, Tid, FileObject, Offset, Size, FastIoAttempt);
        filter.IsRetry(OtherPid, Tid, FileObject, Offset, Size, FastIoAttempt);

        filter.ProcessEnded(Pid);

        Assert.False(filter.IsRetry(Pid, Tid, FileObject, Offset, Size, IrpRetry));
        Assert.True(filter.IsRetry(OtherPid, Tid, FileObject, Offset, Size, IrpRetry));
    }

    [Fact]
    public void IsRetry_PastCapacity_ForgetsEarlierAttempts()
    {
        FastIoRetryFilter filter = new();
        for (int tid = 0; tid < FastIoRetryFilter.Capacity; tid++)
        {
            filter.IsRetry(Pid, tid, FileObject, Offset, Size, FastIoAttempt);
        }

        filter.IsRetry(Pid, FastIoRetryFilter.Capacity, FileObject, Offset, Size, FastIoAttempt);

        Assert.False(filter.IsRetry(Pid, 0, FileObject, Offset, Size, IrpRetry));
        Assert.True(filter.IsRetry(Pid, FastIoRetryFilter.Capacity, FileObject, Offset, Size, IrpRetry));
    }
}
