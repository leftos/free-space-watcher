using FreeSpaceWatcher.Tray.Instance;

namespace FreeSpaceWatcher.Tray.Tests.Instance;

public sealed class TrayInstanceChannelTests
{
    [Fact]
    public void Dispose_BlockedOnTheUiThread_StopsWithoutNeedingThatThread()
    {
        // The app disposes the channel on the UI thread and blocks that thread until it stops, so the dispatcher never runs
        // anything posted to it meanwhile; a context that drops every post stands in for it.
        var channel = TrayInstanceChannel.Listen($"FreeSpaceWatcher.Tray.Test.{Guid.NewGuid():N}", _ => { }, new FakeTrayLog());
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new BlockedThreadContext());
        try
        {
            // Blocking is the case under test: App.Dispose waits synchronously for the channel to stop.
#pragma warning disable xUnit1031
            Assert.True(channel.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
#pragma warning restore xUnit1031
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class BlockedThreadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("The thread is blocked.");
    }
}
