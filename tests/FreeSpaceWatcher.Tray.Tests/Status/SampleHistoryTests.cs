using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Tests.Status;

public sealed class SampleHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 14, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan Span = TimeSpan.FromSeconds(600);

    [Fact]
    public void Trim_KeepsExactlyTheSpan_AndDropsOlder()
    {
        IReadOnlyList<DriveSample> trimmed = SampleHistory.Trim([At(-601), At(-600), At(-10), At(0)], Now, Span);

        Assert.Equal([Now.AddSeconds(-600), Now.AddSeconds(-10), Now], trimmed.Select(s => s.Time));
    }

    [Fact]
    public void Trim_Empty_StaysEmpty() => Assert.Empty(SampleHistory.Trim([], Now, Span));

    [Fact]
    public void Append_AddsTheSample_AndTrimsRelativeToIt()
    {
        IReadOnlyList<DriveSample> history = SampleHistory.Append([At(-605), At(-300)], At(0), Span);

        Assert.Equal([Now.AddSeconds(-300), Now], history.Select(s => s.Time));
    }

    [Fact]
    public void Merge_PutsLoadedHistoryBeforeTheLiveSamples_WithoutOverlap()
    {
        IReadOnlyList<DriveSample> loaded = [At(-700), At(-500), At(-2), At(-1)];
        IReadOnlyList<DriveSample> live = [At(-2), At(0)];

        IReadOnlyList<DriveSample> merged = SampleHistory.Merge(loaded, live, Now, Span);

        Assert.Equal([Now.AddSeconds(-500), Now.AddSeconds(-2), Now], merged.Select(s => s.Time));
    }

    [Fact]
    public void Merge_WithNoLiveSamples_KeepsTheLoadedOnes()
    {
        IReadOnlyList<DriveSample> merged = SampleHistory.Merge([At(-500), At(-1)], [], Now, Span);

        Assert.Equal([Now.AddSeconds(-500), Now.AddSeconds(-1)], merged.Select(s => s.Time));
    }

    private static DriveSample At(int secondsFromNow) => new(Now.AddSeconds(secondsFromNow), 100, 1000);
}
