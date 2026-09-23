using FreeSpaceWatcher.Native;

namespace FreeSpaceWatcher.Service.Tests.Native;

/// <summary>Drives the settle poll of <see cref="ProcessControl.WaitForRunState"/> with a scripted state reader and a recorded wait.</summary>
public sealed class ProcessControlTests
{
    [Fact]
    public void WaitForRunState_RunningThenSuspended_ReturnsSuspendedAfterTwoWaits()
    {
        ProcessRunState[] states = [ProcessRunState.Running, ProcessRunState.Running, ProcessRunState.Suspended];
        int reads = 0;
        List<TimeSpan> waits = [];

        ProcessRunState state = ProcessControl.WaitForRunState(() => states[reads++], ProcessRunState.Suspended, waits.Add);

        Assert.Equal(ProcessRunState.Suspended, state);
        Assert.Equal(3, reads);
        TimeSpan[] expected = [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10)];
        Assert.Equal(expected, waits);
    }

    [Fact]
    public void WaitForRunState_AlwaysRunning_ReturnsRunningOnceTheBudgetIsSpent()
    {
        TimeSpan waited = TimeSpan.Zero;

        ProcessRunState state = ProcessControl.WaitForRunState(() => ProcessRunState.Running, ProcessRunState.Suspended, pause => waited += pause);

        Assert.Equal(ProcessRunState.Running, state);
        Assert.Equal(TimeSpan.FromMilliseconds(500), waited);
    }

    [Fact]
    public void WaitForRunState_Exited_ReturnsAtOnce()
    {
        List<TimeSpan> waits = [];

        ProcessRunState state = ProcessControl.WaitForRunState(() => ProcessRunState.Exited, ProcessRunState.Suspended, waits.Add);

        Assert.Equal(ProcessRunState.Exited, state);
        Assert.Empty(waits);
    }
}
